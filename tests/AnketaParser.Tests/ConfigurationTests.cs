using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AnketaParser.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void JsonFileIsLoadedAndDefaultsAreSafe()
    {
        using var root = new Support.TemporaryContentRoot("""
            {
              "Host": { "InstanceName": "from-json" },
              "Database": { "ConnectionString": "" }
            }
            """);

        var sources = Build(root.Path, out var configuration);
        var host = configuration.GetSection(HostSettings.SectionName).Get<HostSettings>();

        Assert.NotNull(host);
        Assert.Equal("from-json", host.InstanceName);
        Assert.Equal(20, host.ShutdownTimeoutSeconds);
        Assert.Contains(sources, source => source is { Name: "appsettings.json", Kind: "file" });
        Assert.Contains(sources, source => source.Kind == "environment");
    }

    [Fact]
    public void MissingDirectoryStillProducesUsableConfiguration()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anketa-parser-tests", Guid.NewGuid().ToString("N"));

        var sources = Build(missing, out var configuration);

        Assert.Empty(configuration.GetSection(HostSettings.SectionName).GetChildren());
        Assert.Single(sources);
        Assert.DoesNotContain(sources, source => source.Kind == "file");
    }

    [Fact]
    public void EnvironmentVariablesOverrideJsonFile()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Host": { "InstanceName": "from-json" } }
            """);

        const string variable = "ANKETAPARSER_Host__InstanceName";
        Environment.SetEnvironmentVariable(variable, "from-environment");
        try
        {
            Build(root.Path, out var configuration);
            var host = configuration.GetSection(HostSettings.SectionName).Get<HostSettings>();

            Assert.NotNull(host);
            Assert.Equal("from-environment", host.InstanceName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(0, "Host:ShutdownTimeoutSeconds must be between 1 and 600 but was 0.")]
    [InlineData(-5, "Host:ShutdownTimeoutSeconds must be between 1 and 600 but was -5.")]
    public void InvalidShutdownTimeoutIsRejected(int value, string expectedFailure)
    {
        var result = ValidateHostSettings(new HostSettings { ShutdownTimeoutSeconds = value });

        Assert.False(result.Succeeded);
        Assert.Contains(expectedFailure, result.Failures ?? []);
    }

    [Fact]
    public void EmptyInstanceNameIsRejected()
    {
        var result = ValidateHostSettings(new HostSettings { InstanceName = "   " });

        Assert.False(result.Succeeded);
        Assert.Contains("Host:InstanceName must not be empty.", result.Failures ?? []);
    }

    [Fact]
    public void RequiredDatabaseWithoutConnectionStringIsRejected()
    {
        var result = new DatabaseSettingsValidator().Validate(
            Options.DefaultName,
            new DatabaseSettings { Required = true, ConnectionString = string.Empty });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("Database:Required is true", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedConnectionStringIsRejectedWithoutEchoingIt()
    {
        const string secret = "not-a-connection-string;Password=super-secret";
        var result = new DatabaseSettingsValidator().Validate(
            Options.DefaultName,
            new DatabaseSettings { ConnectionString = secret });

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures!);
        Assert.DoesNotContain("super-secret", failure, StringComparison.Ordinal);
        Assert.Contains("Database:ConnectionString is not a valid PostgreSQL connection string", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidConnectionStringPassesValidation()
    {
        var result = new DatabaseSettingsValidator().Validate(
            Options.DefaultName,
            new DatabaseSettings
            {
                ConnectionString = "Host=localhost;Port=5432;Database=anketa_parser;Username=anketa_parser;Password=secret",
                Required = true,
            });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InvalidProbeTimeoutIsRejected()
    {
        var result = new DiagnosticsSettingsValidator().Validate(
            Options.DefaultName,
            new DiagnosticsSettings { ProbeTimeoutSeconds = 0 });

        Assert.False(result.Succeeded);
        Assert.Contains("Diagnostics:ProbeTimeoutSeconds must be between 1 and 120 but was 0.", result.Failures ?? []);
    }

    private static ValidateOptionsResult ValidateHostSettings(HostSettings settings) =>
        new HostSettingsValidator().Validate(Options.DefaultName, settings);

    private static IReadOnlyList<AnketaParser.Core.Diagnostics.ConfigurationSourceInfo> Build(
        string contentRoot,
        out IConfigurationRoot configuration)
    {
        var builder = new ConfigurationBuilder();
        var sources = AnketaParserConfiguration.Apply(builder, contentRoot, "Production");
        configuration = builder.Build();
        return sources;
    }
}