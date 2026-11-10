using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnketaParser.Host;

/// <summary>
/// Composition root of the long-running host: configuration pipeline, logging, typed options and
/// lifecycle services. The host owns the dependency graph; libraries only provide adapters.
/// </summary>
public static class HostApplication
{
    /// <summary>Host process entry point, separated from <c>Program</c> so tests can run it in-process.</summary>
    /// <param name="args">Process arguments; not interpreted as configuration.</param>
    /// <param name="cancellationToken">Stops the host gracefully when cancelled.</param>
    /// <param name="contentRootPath">Optional content root override, used by tests.</param>
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        string? contentRootPath = null)
    {
        try
        {
            using var host = CreateHost(args, contentRootPath);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OptionsValidationException exception)
        {
            WriteConfigurationErrors(exception);
            return ExitCodes.InvalidConfiguration;
        }
        catch (DependencyUnavailableException exception)
        {
            Console.Error.WriteLine($"Dependency unavailable: {exception.Message}");
            return ExitCodes.DependencyUnavailable;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fatal error: {exception.GetType().Name}: {exception.Message}");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Builds the host without starting it. Exposed for tests and for composition checks.
    /// </summary>
    /// <param name="args">Process arguments. They are not treated as configuration on purpose.</param>
    /// <param name="contentRootPath">Content root override; defaults to the application directory.</param>
    /// <returns>Configured, build host instance.</returns>
    public static IHost CreateHost(string[] args, string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var contentRoot = contentRootPath ?? AppContext.BaseDirectory;
        var environmentName = AnketaParserConfiguration.ResolveEnvironmentName();

        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = contentRoot,
            EnvironmentName = environmentName,
        });

        builder.Configuration.Sources.Clear();
        var configurationSources = AnketaParserConfiguration.Apply(
            builder.Configuration,
            contentRoot,
            environmentName);

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddConsole();

        builder.Services.AddAnketaParserInfrastructure(
            builder.Configuration,
            environmentName,
            configurationSources);

        var shutdownTimeout = ConfigurationValues.TryGetInt(
            builder.Configuration,
            "Host:ShutdownTimeoutSeconds",
            20);

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeout);
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        builder.Services.AddHostedService<StartupDiagnosticsService>();
        builder.Services.AddHostedService<ReadinessMonitorService>();

        return builder.Build();
    }

    private static void WriteConfigurationErrors(OptionsValidationException exception)
    {
        Console.Error.WriteLine("Configuration is invalid. Correction is required before startup:");
        foreach (var failure in exception.Failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }
    }
}