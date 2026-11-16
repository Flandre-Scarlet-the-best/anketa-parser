using System.Text.Json;

using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
    public void RuntimeSnapshotProviderReadsHostSettingsAfterConfigurationReload()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host:InstanceName"] = "before-reload",
        });
        var services = new ServiceCollection();
        services.AddAnketaParserInfrastructure(configuration, "Test", []);
        using var provider = services.BuildServiceProvider();
        var snapshots = provider.GetRequiredService<IRuntimeSnapshotProvider>();

        Assert.Equal("before-reload", snapshots.Capture().InstanceName);
        configuration["Host:InstanceName"] = "after-reload";
        ((IConfigurationRoot)configuration).Reload();
        Assert.Equal("after-reload", snapshots.Capture().InstanceName);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonFileOpenFailuresBecomeSafeConfigurationValidationErrors(bool unauthorized)
    {
        using var root = new Support.TemporaryContentRoot("{}");
        var builder = new ConfigurationBuilder();
        _ = AnketaParserConfiguration.Apply(builder, root.Path, "Production");
        var fileProvider = new ThrowingFileProvider(unauthorized);
        foreach (var source in builder.Sources.OfType<JsonConfigurationSource>())
        {
            source.FileProvider = fileProvider;
        }

        var exception = Assert.Throws<OptionsValidationException>(
            () => AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(builder.Build));

        Assert.Equal(
            "Не удалось прочитать файл настроек JSON: проверьте синтаксис и права доступа.",
            Assert.Single(exception.Failures));
        Assert.DoesNotContain("sensitive path", string.Join(Environment.NewLine, exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentVariablesOverrideJsonFile()
    {
        const string variable = "ANKETAPARSER_Host__InstanceName";
        var original = Environment.GetEnvironmentVariable(variable);
        var result = await Support.ApplicationProcess.RunAsync(
            "Cli", """{ "Host": { "InstanceName": "from-json" } }""", ["doctor", "--json"],
            new Dictionary<string, string?> { [variable] = "from-environment" });

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("from-environment", document.RootElement.GetProperty("runtime").GetProperty("instanceName").GetString());
        Assert.Equal(original, Environment.GetEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData(0, "Host:ShutdownTimeoutSeconds должен быть в диапазоне от 1 до 600.")]
    [InlineData(-5, "Host:ShutdownTimeoutSeconds должен быть в диапазоне от 1 до 600.")]
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
        Assert.Contains("Host:InstanceName не должен быть пустым.", result.Failures ?? []);
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
            failure => failure.Contains("Database:Required равен true", StringComparison.Ordinal));
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
        Assert.Contains("Database:ConnectionString не является корректной строкой подключения PostgreSQL", failure, StringComparison.Ordinal);
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
        Assert.Contains("Diagnostics:ProbeTimeoutSeconds должен быть в диапазоне от 1 до 120.", result.Failures ?? []);
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

    private sealed class ThrowingFileProvider(bool unauthorized) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => new ThrowingFileInfo(subpath, unauthorized);

        public IDirectoryContents GetDirectoryContents(string subpath) => EmptyDirectoryContents.Instance;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class ThrowingFileInfo(string name, bool unauthorized) : IFileInfo
    {
        public bool Exists => true;

        public long Length => 1;

        public string? PhysicalPath => null;

        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => unauthorized
            ? throw new UnauthorizedAccessException("sensitive path")
            : throw new IOException("sensitive path");
    }

    private sealed class EmptyDirectoryContents : IDirectoryContents
    {
        public static EmptyDirectoryContents Instance { get; } = new();

        public bool Exists => false;

        public IEnumerator<IFileInfo> GetEnumerator() => Enumerable.Empty<IFileInfo>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}