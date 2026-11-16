using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnketaParser.DockerTests;

/// <summary>
/// Сквозной smoke-тест: сборка образа, готовность PostgreSQL, подключение из контейнера приложения и
/// сохранность данных после пересоздания контейнера. Требует Docker и явного включения:
/// <code>ANKETAPARSER_DOCKER_TESTS=1 dotnet test --project tests/AnketaParser.DockerTests</code>
/// </summary>
public sealed class ComposeStackSmokeTests
{
    private const string EnableVariable = "ANKETAPARSER_DOCKER_TESTS";
    private const string DatabaseUser = "smoke_user";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromMinutes(2);

    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComposeStackIsHealthyAndDatabaseDataSurvivesContainerRecreation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            Assert.Skip($"Задайте {EnableVariable}=1 для запуска smoke-теста Docker/PostgreSQL.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var (environmentFile, processEnvironment) = CreateEnvironmentFile();
        var projectName = $"anketa-parser-smoke-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
        var compose = new DockerCli(repositoryRoot, processEnvironment);
        string[] composePrefix =
        [
            "compose",
            "-p", projectName,
            "--env-file", environmentFile,
            "-f", Path.Combine(repositoryRoot, "compose.yaml"),
        ];

        try
        {
            await compose.RunAsync([.. composePrefix, "up", "-d", "--wait", "postgres"], StartTimeout, _cancellationToken);
            await compose.RunAsync([.. composePrefix, "build", "maintenance", "host"], StartTimeout, _cancellationToken);
            await compose.RunAsync([.. composePrefix, "run", "--rm", "-e",
                $"ANKETAPARSER_Database__ConnectionString=Host=postgres;Database=anketa_parser;Username={DatabaseUser};Password={processEnvironment["POSTGRES_PASSWORD"]}",
                "maintenance", "storage", "provision"], ShortCommandTimeout, _cancellationToken);
            await compose.RunAsync([.. composePrefix, "run", "--rm", "maintenance", "storage", "migrate"], ShortCommandTimeout, _cancellationToken);
            // Оба сервиса должны пройти проверки готовности.
            await compose.RunAsync(
                [.. composePrefix, "up", "-d", "--build", "--wait", "--wait-timeout", "600"],
                StartTimeout,
                _cancellationToken);

            // Порты БД и приложения не должны публиковаться.
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

            // Готовность контейнера подтверждает реальное подключение его проверки к PostgreSQL.
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

            // Копия должна появиться от фонового сервиса без ручной команды backup.
            var automaticBackupCreated = false;
            for (var attempt = 0; attempt < 15 && !automaticBackupCreated; attempt++)
            {
                var status = await compose.RunAsync([.. composePrefix, "run", "--rm", "maintenance", "storage", "backup-status"],
                    ShortCommandTimeout, _cancellationToken);
                using var backup = JsonDocument.Parse(status);
                automaticBackupCreated = backup.RootElement.GetProperty("LastSuccess").ValueKind == JsonValueKind.String;
                if (!automaticBackupCreated) await Task.Delay(TimeSpan.FromSeconds(1), _cancellationToken);
            }
            Assert.True(automaticBackupCreated, "Фоновый сервис должен создать резервную копию автоматически.");

            // Маркер хранится в одноразовой тестовой БД и не создаёт продуктовую схему.
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
                "INSERT INTO smoke_marker (id, note) VALUES (1, 'survives-recreation');");

            // Пересоздание контейнера не должно терять данные именованного тома.
            await compose.RunAsync(
                [.. composePrefix, "up", "-d", "--no-deps", "--force-recreate", "--wait", "--wait-timeout", "300", "postgres"],
                StartTimeout,
                _cancellationToken);
            var recreatedPostgresContainerId = await compose.RunAsync(
                [.. composePrefix, "ps", "-q", "postgres"],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(recreatedPostgresContainerId));
            Assert.NotEqual(postgresContainerId, recreatedPostgresContainerId);

            await compose.RunAsync(
                [.. composePrefix, "up", "-d", "--wait", "--wait-timeout", "300"],
                StartTimeout,
                _cancellationToken);

            var count = await RunPsqlAsync(
                compose,
                composePrefix,
                "anketa_parser_smoke",
                "SELECT count(*) FROM smoke_marker WHERE note = 'survives-recreation';");
            Assert.Equal("1", count);

            // После пересоздания БД приложение должно остаться готовым.
            var healthAfterRecreation = await compose.RunAsync(
                ["inspect", "--format", "{{.State.Health.Status}}", hostContainerId],
                ShortCommandTimeout,
                _cancellationToken);
            Assert.Equal("healthy", healthAfterRecreation);
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
                "-U", DatabaseUser,
                "-d", database,
                "-tAc", sql,
            ],
            ShortCommandTimeout,
            _cancellationToken);
    }

    internal static string FindRepositoryRoot()
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

        throw new InvalidOperationException("compose.yaml не найден в родительских каталогах теста.");
    }

    private static (string Path, IReadOnlyDictionary<string, string> Variables) CreateEnvironmentFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"anketa-parser-smoke-{Guid.NewGuid():N}.env"));

        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["POSTGRES_DB"] = "anketa_parser",
            ["POSTGRES_USER"] = DatabaseUser,
            ["POSTGRES_PASSWORD"] = RandomNumberGenerator.GetHexString(24, lowercase: true),
        };
        File.WriteAllText(
            path,
            string.Join(Environment.NewLine, variables.Select(pair => $"{pair.Key}={pair.Value}")) +
            Environment.NewLine);

        return (path, variables);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Оставшийся временный файл не должен прерывать тест.
        }
    }
}