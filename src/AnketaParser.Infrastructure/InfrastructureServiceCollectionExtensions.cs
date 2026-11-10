using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure;

/// <summary>
/// Registers infrastructure adapters. Each executable host remains the composition root and decides
/// which of these capabilities, plus its own lifecycle services, it actually wires up.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Adds typed options with validation, the readiness probes, the diagnostics runner and the
    /// runtime snapshot provider.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">Effective configuration.</param>
    /// <param name="environmentName">Resolved hosting environment name.</param>
    /// <param name="configurationSources">Sources reported by diagnostics output.</param>
    public static IServiceCollection AddAnketaParserInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName,
        IReadOnlyList<ConfigurationSourceInfo> configurationSources)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(configurationSources);

        services.AddOptions<HostSettings>()
            .Bind(configuration.GetSection(HostSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostSettings>, HostSettingsValidator>();

        services.AddOptions<DatabaseSettings>()
            .Bind(configuration.GetSection(DatabaseSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseSettings>, DatabaseSettingsValidator>();

        services.AddOptions<DiagnosticsSettings>()
            .Bind(configuration.GetSection(DiagnosticsSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DiagnosticsSettings>, DiagnosticsSettingsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new EnvironmentDescriptor(environmentName));
        services.AddSingleton(new ConfigurationSourceSet(configurationSources));
        services.TryAddSingleton<ReadinessStateStore>();

        services.TryAddSingleton<IRuntimeSnapshotProvider, RuntimeSnapshotProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessProbe, PostgresReadinessProbe>());
        services.TryAddSingleton<DiagnosticsRunner>();

        return services;
    }
}