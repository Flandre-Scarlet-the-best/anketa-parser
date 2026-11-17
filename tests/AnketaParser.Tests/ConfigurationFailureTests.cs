using System.Text.Json;

using AnketaParser.Cli;
using AnketaParser.Core;

namespace AnketaParser.Tests;

public sealed class ConfigurationFailureTests
{
    private const string Secret = "configuration-secret-sentinel";

    [Theory]
    [InlineData("Cli", "json", "")]
    [InlineData("Host", "json", "")]
    [InlineData("Cli", "int", "ANKETAPARSER_Diagnostics__ProbeTimeoutSeconds")]
    [InlineData("Host", "int", "ANKETAPARSER_Diagnostics__ProbeTimeoutSeconds")]
    [InlineData("Cli", "bool", "ANKETAPARSER_Database__Required")]
    [InlineData("Host", "bool", "ANKETAPARSER_Database__Required")]
    [InlineData("Cli", "bool", "ANKETAPARSER_Host__StartupDiagnosticsEnabled")]
    [InlineData("Host", "bool", "ANKETAPARSER_Host__StartupDiagnosticsEnabled")]
    [InlineData("Cli", "int", "ANKETAPARSER_Host__ShutdownTimeoutSeconds")]
    [InlineData("Host", "int", "ANKETAPARSER_Host__ShutdownTimeoutSeconds")]
    [InlineData("Cli", "int", "ANKETAPARSER_Database__ConnectTimeoutSeconds")]
    [InlineData("Host", "int", "ANKETAPARSER_Database__ConnectTimeoutSeconds")]
    [InlineData("Cli", "int", "ANKETAPARSER_Backup__TimeoutSeconds")]
    [InlineData("Host", "int", "ANKETAPARSER_Backup__TimeoutSeconds")]
    public async Task InvalidInputsHaveSafeConfigurationExitCode(string application, string kind, string variable)
    {
        var json = kind == "json" ? $"{{\"{Secret}\": invalid" : "{}";
        var environment = new Dictionary<string, string?>();
        if (variable.Length > 0)
        {
            environment[variable] = Secret;
        }

        var result = await Support.ApplicationProcess.RunAsync(application, json, ["doctor", "--json"], environment);

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.DoesNotContain(Secret, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.Contains(kind == "json" ? "JSON" : "тип", result.Stderr, StringComparison.Ordinal);
        if (application == "Cli")
        {
            using var document = JsonDocument.Parse(result.Stdout);
            Assert.Equal("invalid_configuration", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.False(document.RootElement.GetProperty("configuration").GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("configuration").GetProperty("database").ValueKind);
            Assert.NotEmpty(document.RootElement.GetProperty("errors").EnumerateArray());
        }
        else
        {
            Assert.Empty(result.Stdout);
        }
    }

    [Fact]
    public async Task UnexpectedOutputFailureKeepsInternalExitCodeAndHidesExceptionText()
    {
        using var root = new Support.TemporaryContentRoot("{}");
        using var stdout = new FailingWriter();
        using var stderr = new StringWriter();

        var code = await CliApplication.RunAsync(["doctor", "--json"], stdout, stderr,
            TestContext.Current.CancellationToken, root.Path);

        Assert.Equal(ExitCodes.UnexpectedError, code);
        Assert.Contains("IOException", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cli", true)]
    [InlineData("Host", true)]
    [InlineData("Cli", false)]
    [InlineData("Host", false)]
    public async Task ConfigurationValidationAndConnectionFailuresHideSecretsInRealStreams(string application, bool invalidConnectionString)
    {
        var connectionString = invalidConnectionString
            ? $"invalid-{Secret}"
            : $"Host=127.0.0.1;Port=1;Database=synthetic;Username=test;Password={Secret}";
        var json = JsonSerializer.Serialize(new
        {
            Database = new { ConnectionString = connectionString, Required = true, ConnectTimeoutSeconds = 1 },
        });

        var result = await Support.ApplicationProcess.RunAsync(application, json, ["doctor", "--json"]);

        Assert.Equal(invalidConnectionString ? 3 : 4, result.ExitCode);
        Assert.DoesNotContain(Secret, result.Stdout + result.Stderr, StringComparison.Ordinal);
        if (application == "Cli")
        {
            using var document = JsonDocument.Parse(result.Stdout);
            Assert.Equal(invalidConnectionString ? "invalid_configuration" : "unhealthy",
                document.RootElement.GetProperty("status").GetString());
        }
        else
        {
            Assert.Contains(invalidConnectionString ? "Конфигурация некорректна" : "Обязательная зависимость недоступна",
                result.Stderr, StringComparison.Ordinal);
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new IOException(Secret);
    }
}