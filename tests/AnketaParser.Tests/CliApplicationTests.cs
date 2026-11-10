using System.Text.Json;

using AnketaParser.Cli;
using AnketaParser.Core;

namespace AnketaParser.Tests;

public sealed class CliApplicationTests
{
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args,
        string? contentRootPath = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            args,
            stdout,
            stderr,
            TestContext.Current.CancellationToken,
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
        Assert.Contains("Exit codes:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("doctor", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOptionFailsWithUsageExitCode()
    {
        var result = await RunAsync(["--bogus"]);

        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        Assert.Contains("error:", result.Stderr, StringComparison.Ordinal);
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
        Assert.Contains("frobnicate", result.Stderr, StringComparison.Ordinal);
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
        Assert.Contains("configuration is invalid", result.Stderr, StringComparison.Ordinal);
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
        Assert.Contains("status      : degraded", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("probe postgres", result.Stdout, StringComparison.Ordinal);
    }
}