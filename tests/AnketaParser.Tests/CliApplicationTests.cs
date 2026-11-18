using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using AnketaParser.Cli;
using AnketaParser.Core;

namespace AnketaParser.Tests;

public sealed class CliApplicationTests
{
    [Theory]
    [InlineData("post", "секретное значение", ExitCodes.InvalidUsage, "invalid_usage")]
    [InlineData("scan", "https://example.invalid", ExitCodes.InvalidUsage, "invalid_usage")]
    [InlineData("status", "--json", ExitCodes.InvalidConfiguration, "not_configured")]
    public async Task SourceErrorsHaveOneSafeMachineDocument(string command, string argument, int code, string status)
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var result = await RunAsync(["source", command, argument], root.Path);
        Assert.Equal(code, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(code, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(argument, result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceReportsRefusedPostgresConnectionAsUnavailable()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        using var root = new Support.TemporaryContentRoot($$"""
            {
              "Dvach": { "ThreadUrl": "https://2ch.org/soc/res/100000.html" },
              "Database": { "ConnectionString": "Host=127.0.0.1;Port={{port}};Database=fixture;Username=fixture;Password=fixture_secret;Timeout=1" }
            }
            """);
        foreach (var command in new[] { "status", "scan" })
        {
            var result = await RunAsync(["source", command], root.Path);
            Assert.Equal(ExitCodes.DependencyUnavailable, result.ExitCode);
            Assert.DoesNotContain("fixture_secret", result.Stdout + result.Stderr, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(result.Stdout);
            var expected = command == "scan" ? document.RootElement.GetProperty("result").GetProperty("failure") :
                document.RootElement.GetProperty("status");
            Assert.Equal("database_unavailable", expected.GetString());
        }
    }

    [Fact]
    public async Task StorageHelpBypassesInvalidConfigurationAndUnknownCommandIsUsageError()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        foreach (var json in new[] { "{}", "{неверный JSON}", """{"Database":{"ConnectionString":"invalid"},"Backup":{"Directory":null,"TimeoutSeconds":0}}""" })
        {
            await File.WriteAllTextAsync(Path.Combine(root.Path, "appsettings.json"), json, TestContext.Current.CancellationToken);
            var result = await RunAsync(["storage", "help"], root.Path);
            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Contains("restore-empty", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("backup-daemon", result.Stdout, StringComparison.Ordinal);
            Assert.Empty(result.Stderr);
            Assert.Equal(ExitCodes.InvalidUsage, (await RunAsync(["storage", "unknown"], root.Path)).ExitCode);
        }
    }

    [Theory]
    [InlineData("Host", "ShutdownTimeoutSeconds")]
    [InlineData("Database", "ConnectTimeoutSeconds")]
    [InlineData("Diagnostics", "ProbeTimeoutSeconds")]
    [InlineData("Backup", "TimeoutSeconds")]
    public async Task StorageUsesSharedTypedValidators(string section, string key)
    {
        using var root = new Support.TemporaryContentRoot($$"""
            { "{{section}}": { "{{key}}": 0 }, "Backup": { "Directory": "unused" } }
            """);
        var result = await RunAsync(["storage", "backup-list"], root.Path);
        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task StorageCancellationIsFailureExceptForBackupDaemonShutdown()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Backup": { "Directory": "unused" } }
            """);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var operation = await RunAsync(["storage", "backup"], root.Path, cancellation.Token);
        Assert.Equal(ExitCodes.UnexpectedError, operation.ExitCode);
        Assert.Empty(operation.Stdout);
        Assert.Contains("остановлена", operation.Stderr, StringComparison.Ordinal);

        var daemon = await RunAsync(["storage", "backup-daemon"], root.Path, cancellation.Token);
        Assert.Equal(ExitCodes.Success, daemon.ExitCode);
        Assert.Empty(daemon.Stdout);
    }

    [Theory]
    [InlineData("backup-verify", "../outside.dump")]
    [InlineData("restore-empty", "../outside.dump")]
    [InlineData("backup-verify", "outside.txt")]
    public async Task StorageInvalidArchiveIsUsageError(string command, string archive)
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Backup": { "Directory": "unused" } }
            """);
        var result = await RunAsync(["storage", command, archive], root.Path);
        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        Assert.Empty(result.Stdout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args,
        string? contentRootPath = null,
        CancellationToken? cancellationToken = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            args,
            stdout,
            stderr,
            cancellationToken ?? TestContext.Current.CancellationToken,
            contentRootPath);

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task VersionPrintsHumanText()
    {
        var result = await RunAsync(["--version"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith("anketa-parser ", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task VersionInMachineModeIsSingleJsonDocument()
    {
        var result = await RunAsync(["version", "--json"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("version", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(ExitCodes.Success, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task HelpDocumentsExitCodes()
    {
        var result = await RunAsync(["--help"]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Коды завершения:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("doctor", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOptionFailsWithUsageExitCode()
    {
        var result = await RunAsync(["--bogus"]);

        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        Assert.Contains("Ошибка:", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task UnknownCommandInMachineModeStillWritesOneJsonDocument()
    {
        var result = await RunAsync(["--json", "frobnicate"]);

        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("invalid_usage", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(ExitCodes.InvalidUsage, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("Неизвестная команда", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithoutDatabaseReportsDegradedAndExitZero()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Database": { "ConnectionString": "", "Required": false } }
            """);

        var result = await RunAsync(["doctor", "--json"], root.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var rootElement = document.RootElement;
        Assert.Equal("doctor", rootElement.GetProperty("command").GetString());
        Assert.Equal("degraded", rootElement.GetProperty("status").GetString());
        Assert.True(rootElement.GetProperty("configuration").GetProperty("valid").GetBoolean());
        Assert.False(rootElement.GetProperty("configuration").GetProperty("database").GetProperty("configured").GetBoolean());

        var probe = Assert.Single(rootElement.GetProperty("probes").EnumerateArray().ToList());
        Assert.Equal("postgres", probe.GetProperty("name").GetString());
        Assert.Equal("skipped", probe.GetProperty("status").GetString());
        Assert.Equal(0, rootElement.GetProperty("probes").GetArrayLength() - 1);
    }

    [Fact]
    public async Task DoctorWithInvalidConfigurationReturnsConfigurationExitCode()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Host": { "ShutdownTimeoutSeconds": 0 } }
            """);

        var result = await RunAsync(["doctor", "--json"], root.Path);

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var rootElement = document.RootElement;
        Assert.Equal("invalid_configuration", rootElement.GetProperty("status").GetString());
        Assert.False(rootElement.GetProperty("configuration").GetProperty("valid").GetBoolean());
        Assert.Contains(
            rootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("ShutdownTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains("конфигурация некорректна", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorWithUnreachableDatabaseReturnsDependencyExitCodeAndHidesPassword()
    {
        using var root = new Support.TemporaryContentRoot("""
            {
              "Database": {
                "ConnectionString": "Host=127.0.0.1;Port=1;Database=anketa_parser;Username=anketa_parser;Password=super-secret",
                "Required": true,
                "ConnectTimeoutSeconds": 1,
                "CommandTimeoutSeconds": 1
              },
              "Diagnostics": { "ProbeTimeoutSeconds": 10 }
            }
            """);

        var result = await RunAsync(["doctor", "--json"], root.Path);

        Assert.Equal(ExitCodes.DependencyUnavailable, result.ExitCode);
        Assert.DoesNotContain("super-secret", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.Stderr, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.Stdout);
        var rootElement = document.RootElement;
        Assert.Equal("unhealthy", rootElement.GetProperty("status").GetString());
        Assert.True(rootElement.GetProperty("configuration").GetProperty("database").GetProperty("configured").GetBoolean());

        var probe = Assert.Single(rootElement.GetProperty("probes").EnumerateArray().ToList());
        Assert.Equal("unhealthy", probe.GetProperty("status").GetString());
        Assert.Contains("127.0.0.1:1/anketa_parser", probe.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorHumanModeWritesStatusToStdout()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Database": { "ConnectionString": "" } }
            """);

        var result = await RunAsync(["doctor"], root.Path);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("статус      : degraded", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("проверка postgres", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledDoctorReturnsFailureAndOneJsonDocument()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await RunAsync(["doctor", "--json"], root.Path, cancellation.Token);

        Assert.Equal(ExitCodes.UnexpectedError, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("doctor", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("unhealthy", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(ExitCodes.UnexpectedError, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("Диагностика остановлена.", Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray()).GetString());
        Assert.Contains("Диагностика остановлена.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorCompletesJsonPublicationWhenCancellationArrivesAfterWriting()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        using var cancellation = new CancellationTokenSource();
        using var stdout = new CancellingWriter(cancellation);
        using var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["doctor", "--json"], stdout, stderr, cancellation.Token, root.Path);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(ExitCodes.Success, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("degraded", document.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("doctor", "version")]
    [InlineData("version", "doctor")]
    [InlineData("help", "doctor")]
    [InlineData("help", "help")]
    [InlineData("doctor", "doctor")]
    [InlineData("doctor", "--help")]
    public async Task MultipleCommandsAreInvalidUsageRegardlessOfJsonPosition(string first, string second)
    {
        foreach (string[] args in new[] { new[] { "--json", first, second }, new[] { first, second, "--json" } })
        {
            var result = await RunAsync(args);
            Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
            using var document = JsonDocument.Parse(result.Stdout);
            Assert.Equal("invalid_usage", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("только одна команда", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--json", "version")]
    [InlineData("version", "--json")]
    [InlineData("--json", "doctor")]
    [InlineData("doctor", "--json")]
    public async Task JsonFlagWorksBeforeAndAfterCommand(string first, string second)
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var result = await RunAsync([first, second], root.Path);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(first == "--json" ? second : first, document.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task VersionAndDoctorShareSchemaVersion()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        using var version = JsonDocument.Parse((await RunAsync(["version", "--json"])).Stdout);
        using var doctor = JsonDocument.Parse((await RunAsync(["doctor", "--json"], root.Path)).Stdout);
        Assert.Equal(1, doctor.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(doctor.RootElement.GetProperty("schemaVersion").GetInt32(), version.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task EmptyInvocationShowsSupportedLaunchCommand()
    {
        var result = await RunAsync([]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("dotnet AnketaParser.Cli.dll", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("anketa-parser-cli", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-command")]
    [InlineData("--unknown-option")]
    public async Task UnknownArgumentRecognizesTrailingJsonFlag(string argument)
    {
        var result = await RunAsync([argument, "--json"]);
        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("invalid_usage", document.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    public async Task HelpWithJsonFlagRemainsText(string command)
    {
        var result = await RunAsync(["--json", command]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Команды:", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(result.Stderr);
    }

    private sealed class CancellingWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            var write = base.WriteLineAsync(value);
            cancellation.Cancel();
            return write;
        }
    }
}