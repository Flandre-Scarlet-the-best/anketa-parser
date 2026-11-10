using System.Globalization;
using System.Security.Cryptography;

namespace AnketaParser.DockerTests;

/// <summary>
/// End-to-end smoke test of the container infrastructure: compose configuration, image build,
/// PostgreSQL health, real connectivity from the application container and data survival across a
/// container restart.
///
/// The test is skipped by default because it requires a working Docker engine. Enable it explicitly:
/// <code>ANKETAPARSER_DOCKER_TESTS=1 dotnet test tests/AnketaParser.DockerTests</code>
/// </summary>
public sealed class ComposeStackSmokeTests
{
    private const string EnableVariable = "ANKETAPARSER_DOCKER_TESTS";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromMinutes(2);

    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComposeStackIsHealthyAndDatabaseDataSurvivesRestart()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            Assert.Skip($"Set {EnableVariable}=1 to run the Docker/PostgreSQL smoke test.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var environmentFile = CreateEnvironmentFile();
        var projectName = $"anketa-parser-smoke-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
        var compose = new DockerCli(repositoryRoot);
        string[] composePrefix =
        [
            "compose",
            "-p", projectName,
            "--env-file", environmentFile,
            "-f", Path.Combine(repositoryRoot, "compose.yaml"),
        ];

        try
        {
            // 1. The stack starts and both services reach their health checks.
            await compose.RunAsync(
                [.. composePrefix, "up", "-d", "--build", "--wait", "--wait-timeout", "600"],
                StartTimeout,
                _cancellationToken);

            // 2. No port is published for the database or the application.
            var postgresContainerId = await compose.RunAsync(
                [.. composePrefix, "ps", "-q", "postgres"],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(postgresContainerId));

            var publishedPorts = await compose.RunAsync(
                ["port", postgresContainerId],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.Equal(string.Empty, publishedPorts);

            // 3. The application container reports healthy, which means the readiness probe inside
            //    the container really connected to PostgreSQL.
            var hostContainerId = await compose.RunAsync(
                [.. composePrefix, "ps", "-q", "host"],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(hostContainerId));

            var health = await compose.RunAsync(
                ["inspect", "--format", "{{.State.Health.Status}}", hostContainerId],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.Equal("healthy", health);

            // 4. Write a marker row into a throw-away database. This is test-only state: the product
            //    itself has no schema at this stage.
            await RunPsqlAsync(
                compose,
                composePrefix,
                "postgres",
                "CREATE DATABASE anketa_parser_smoke");
            await RunPsqlAsync(
                compose,
                composePrefix,
                "anketa_parser_smoke",
                "CREATE TABLE smoke_marker (id integer PRIMARY KEY, note text NOT NULL); " +
                "INSERT INTO smoke_marker (id, note) VALUES (1, 'survives-restart');");

            // 5. A normal container restart must not lose data stored in the named volume.
            await compose.RunAsync(
                [.. composePrefix, "restart", "postgres"],
                ShortCommandTimeout,
                _cancellationToken);
            await compose.RunAsync(
                [.. composePrefix, "up", "-d", "--wait", "--wait-timeout", "300"],
                StartTimeout,
                _cancellationToken);

            var count = await RunPsqlAsync(
                compose,
                composePrefix,
                "anketa_parser_smoke",
                "SELECT count(*) FROM smoke_marker WHERE note = 'survives-restart';");
            Assert.Equal("1", count);

            // 6. The application is still healthy after the database restart.
            var healthAfterRestart = await compose.RunAsync(
                ["inspect", "--format", "{{.State.Health.Status}}", hostContainerId],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.Equal("healthy", healthAfterRestart);
        }
        finally
        {
            await compose.RunAsync(
                [.. composePrefix, "down", "--volumes", "--remove-orphans"],
                ShortCommandTimeout,
                CancellationToken.None,
                requireSuccess: false);

            TryDelete(environmentFile);
        }
    }

    private async Task<string> RunPsqlAsync(
        DockerCli compose,
        string[] composePrefix,
        string database,
        string sql)
    {
        return await compose.RunAsync(
            [
                .. composePrefix,
                "exec", "-T", "postgres",
                "psql", "-v", "ON_ERROR_STOP=1",
                "-U", "anketa_parser",
                "-d", database,
                "-tAc", sql,
            ],
            ShortCommandTimeout,
            _cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("compose.yaml was not found above the test directory.");
    }

    private static string CreateEnvironmentFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"anketa-parser-smoke-{Guid.NewGuid():N}.env"));

        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);
        File.WriteAllText(
            path,
            $"POSTGRES_DB=anketa_parser{Environment.NewLine}" +
            $"POSTGRES_USER=anketa_parser{Environment.NewLine}" +
            $"POSTGRES_PASSWORD={password}{Environment.NewLine}");

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file must not fail the test.
        }
    }
}