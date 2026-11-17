using System.Text.Json;

using Npgsql;

namespace AnketaParser.DockerTests;

public sealed class DockerCliTests
{
    [Fact]
    public async Task RunAsyncPassesConfiguredEnvironmentToChildProcess()
    {
        const string variableName = "POSTGRES_USER";
        const string expectedValue = "smoke_user";
        var environment = new Dictionary<string, string> { [variableName] = expectedValue };
        var isWindows = OperatingSystem.IsWindows();
        var executable = isWindows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        var arguments = isWindows
            ? new[] { "/d", "/c", $"echo %{variableName}%" }
            : new[] { "-c", $"printf '%s' \"${variableName}\"" };
        var docker = new DockerCli(Path.GetTempPath(), executable, environment);

        var result = await docker.RunAsync(arguments, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(expectedValue, result);
    }

    [Fact]
    public async Task ComposeConnectionStringsPreservePasswordsWithQuotesAndSemicolons()
    {
        if (!ComposeImageFixture.Enabled)
        {
            Assert.Skip($"Задайте {ComposeImageFixture.EnableVariable}=1 для проверки конфигурации Compose.");
        }

        var root = ComposeStackSmokeTests.FindRepositoryRoot();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PRODUCT_RUNTIME_PASSWORD"] = "runtime;\"synthetic",
            ["PRODUCT_MIGRATION_PASSWORD"] = "migration;\"synthetic",
            ["PRODUCT_BACKUP_PASSWORD"] = "backup;\"synthetic",
        };
        var docker = new DockerCli(root, expected);
        var output = await docker.RunAsync(
            ["compose", "--profile", "maintenance", "-f", Path.Combine(root, "compose.yaml"), "config", "--format", "json"],
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output);
        var services = document.RootElement.GetProperty("services");

        AssertPassword(services.GetProperty("host"), "ANKETAPARSER_Database__ConnectionString", "ANKETAPARSER_Database__Password", expected["PRODUCT_RUNTIME_PASSWORD"]);
        AssertPassword(services.GetProperty("maintenance"), "ANKETAPARSER_Database__ConnectionString", "ANKETAPARSER_Database__Password", expected["PRODUCT_MIGRATION_PASSWORD"]);
        AssertPassword(services.GetProperty("maintenance"), "ANKETAPARSER_Backup__ConnectionString", "ANKETAPARSER_Backup__Password", expected["PRODUCT_BACKUP_PASSWORD"]);
        AssertPassword(services.GetProperty("backup"), "ANKETAPARSER_Backup__ConnectionString", "ANKETAPARSER_Backup__Password", expected["PRODUCT_BACKUP_PASSWORD"]);
    }

    private static void AssertPassword(JsonElement service, string connectionStringSetting, string passwordSetting, string expected)
    {
        var environment = service.GetProperty("environment");
        var connectionString = environment.GetProperty(connectionStringSetting).GetString();
        Assert.DoesNotContain("Password=", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, environment.GetProperty(passwordSetting).GetString());
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = environment.GetProperty(passwordSetting).GetString(),
        }.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitDistinguishesCallerCancellationFromTimeout(bool cancelCaller)
    {
        if (Environment.GetEnvironmentVariable("ANKETAPARSER_DOCKER_TESTS") != "1")
        {
            Assert.Skip("Задайте ANKETAPARSER_DOCKER_TESTS=1 для проверки отмены Docker CLI.");
        }

        var docker = new DockerCli(ComposeStackSmokeTests.FindRepositoryRoot());
        using var caller = new CancellationTokenSource();
        if (cancelCaller)
        {
            caller.CancelAfter(TimeSpan.FromMilliseconds(100));
        }

        // docker events ожидает события до внешней отмены либо собственного таймаута.
        var run = docker.RunAsync(["events", "--filter", "label=anketa-parser-synthetic-cancellation"],
            cancelCaller ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(100), caller.Token);
        if (cancelCaller)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => run);
        }
    }
}