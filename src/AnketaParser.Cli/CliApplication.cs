using System.Text.Json;
using System.Text.Json.Serialization;

using AnketaParser.Cli;
using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace AnketaParser.Cli;

/// <summary>
/// Entry point logic of the agent/administrator CLI. Split from <c>Program</c> so that tests can run
/// commands in-process and assert on stdout, stderr and exit codes.
/// </summary>
public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Runs one CLI invocation.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="stdout">Machine-readable output stream.</param>
    /// <param name="stderr">Human diagnostics stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="contentRootPath">Optional content root override, used by tests.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var info = ApplicationInfo.ForEntryAssembly();
        var invocation = CommandLineParser.Parse(args);

        if (!invocation.IsValid)
        {
            await WriteUsageErrorAsync(invocation, info, stdout, stderr, cancellationToken).ConfigureAwait(false);
            return ExitCodes.InvalidUsage;
        }

        switch (invocation.Command)
        {
            case CliCommand.Help:
                await stdout.WriteLineAsync(HelpText.Usage(info.ProductName, info.Version)).ConfigureAwait(false);
                await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;

            case CliCommand.Version:
                await WriteVersionAsync(invocation, info, stdout, cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;

            case CliCommand.Doctor:
                return await RunDoctorAsync(invocation, info, stdout, stderr, contentRootPath, cancellationToken)
                    .ConfigureAwait(false);

            default:
                await stderr.WriteLineAsync($"Unsupported command '{invocation.Command}'.").ConfigureAwait(false);
                return ExitCodes.UnexpectedError;
        }
    }

    private static async Task WriteUsageErrorAsync(
        CliInvocation invocation,
        ApplicationInfo info,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var message = invocation.Error ?? "Invalid command line usage.";
        await stderr.WriteLineAsync($"error: {message}").ConfigureAwait(false);
        await stderr
            .WriteLineAsync("Run 'anketa-parser-cli --help' to see supported commands and options.")
            .ConfigureAwait(false);
        await stderr.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (!invocation.IsMachineReadable)
        {
            return;
        }

        var document = new DiagnosticsDocument
        {
            Tool = info.ProductName,
            ToolVersion = info.Version,
            Command = "unknown",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Status = "invalid_usage",
            ExitCode = ExitCodes.InvalidUsage,
            Configuration = new ConfigurationDocument
            {
                Valid = false,
                Sources = [],
                Database = new DatabaseDocument
                {
                    Configured = false,
                    Required = false,
                    Target = "(not configured)",
                    ConnectTimeoutSeconds = 0,
                },
            },
            Probes = [],
            Errors = [message],
        };

        await WriteDocumentAsync(document, stdout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteVersionAsync(
        CliInvocation invocation,
        ApplicationInfo info,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (!invocation.IsMachineReadable)
        {
            await stdout.WriteLineAsync($"{info.ProductName} {info.Version}").ConfigureAwait(false);
            await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = new
        {
            schemaVersion = 1,
            tool = info.ProductName,
            toolVersion = info.Version,
            command = "version",
            exitCode = ExitCodes.Success,
        };

        await stdout.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
        await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunDoctorAsync(
        CliInvocation invocation,
        ApplicationInfo info,
        TextWriter stdout,
        TextWriter stderr,
        string? contentRootPath,
        CancellationToken cancellationToken)
    {
        var contentRoot = contentRootPath ?? AppContext.BaseDirectory;
        var environmentName = AnketaParserConfiguration.ResolveEnvironmentName();

        var configuration = new ConfigurationBuilder();
        var sources = AnketaParserConfiguration.Apply(configuration, contentRoot, environmentName);
        var configurationRoot = configuration.Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddConfiguration(configurationRoot.GetSection("Logging"));
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.IncludeScopes = false;
            });
            // CLI diagnostics must never pollute the machine-readable stdout stream.
            builder.AddFilter<ConsoleLoggerProvider>((_, level) => level >= LogLevel.Warning);
        });
        services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);

        services.AddAnketaParserInfrastructure(configurationRoot, environmentName, sources);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });

        try
        {
            var hostSettings = provider.GetRequiredService<IOptions<HostSettings>>().Value;
            var databaseSettings = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            var diagnosticsSettings = provider.GetRequiredService<IOptions<DiagnosticsSettings>>().Value;

            var runner = provider.GetRequiredService<DiagnosticsRunner>();
            var report = await runner
                .RunAsync(sources, TimeSpan.FromSeconds(diagnosticsSettings.ProbeTimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);

            var exitCode = report.Status == HealthStatus.Unhealthy
                ? ExitCodes.DependencyUnavailable
                : ExitCodes.Success;

            var document = CreateDocument(info, report, databaseSettings, exitCode);

            if (invocation.IsMachineReadable)
            {
                await WriteDocumentAsync(document, stdout, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteHumanReportAsync(document, report, stderr, stdout, cancellationToken).ConfigureAwait(false);
            }

            return exitCode;
        }
        catch (OptionsValidationException exception)
        {
            var exitCode = ExitCodes.InvalidConfiguration;
            await stderr.WriteLineAsync("error: configuration is invalid.").ConfigureAwait(false);
            foreach (var failure in exception.Failures)
            {
                await stderr.WriteLineAsync($"  - {failure}").ConfigureAwait(false);
            }

            if (invocation.IsMachineReadable)
            {
                var document = new DiagnosticsDocument
                {
                    Tool = info.ProductName,
                    ToolVersion = info.Version,
                    Command = "doctor",
                    GeneratedAtUtc = DateTimeOffset.UtcNow,
                    Status = "invalid_configuration",
                    ExitCode = exitCode,
                    Configuration = new ConfigurationDocument
                    {
                        Valid = false,
                        Sources = sources.Select(static source => new ConfigurationSourceDocument
                        {
                            Name = source.Name,
                            Kind = source.Kind,
                        }).ToArray(),
                        Database = new DatabaseDocument
                        {
                            Configured = false,
                            Required = false,
                            Target = "(not configured)",
                            ConnectTimeoutSeconds = 0,
                        },
                    },
                    Probes = [],
                    Errors = [.. exception.Failures],
                };

                await WriteDocumentAsync(document, stdout, cancellationToken).ConfigureAwait(false);
            }

            return exitCode;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr
                .WriteLineAsync($"error: unexpected failure: {exception.GetType().Name}: {exception.Message}")
                .ConfigureAwait(false);
            return ExitCodes.UnexpectedError;
        }
    }

    private static DiagnosticsDocument CreateDocument(
        ApplicationInfo info,
        DiagnosticReport report,
        DatabaseSettings database,
        int exitCode)
    {
        return new DiagnosticsDocument
        {
            Tool = info.ProductName,
            ToolVersion = info.Version,
            Command = "doctor",
            GeneratedAtUtc = report.GeneratedAtUtc,
            Status = report.Status switch
            {
                HealthStatus.Healthy => "healthy",
                HealthStatus.Degraded => "degraded",
                _ => "unhealthy",
            },
            ExitCode = exitCode,
            Runtime = new RuntimeDocument
            {
                Framework = report.Runtime.FrameworkDescription,
                Os = report.Runtime.OsDescription,
                Architecture = report.Runtime.Architecture,
                IsContainer = report.Runtime.IsContainer,
                MachineName = report.Runtime.MachineName,
                ProcessId = report.Runtime.ProcessId,
                UptimeSeconds = report.Runtime.Uptime?.TotalSeconds,
                InstanceName = report.Runtime.InstanceName,
                Environment = report.Runtime.EnvironmentName,
            },
            Configuration = new ConfigurationDocument
            {
                Valid = true,
                Sources = report.ConfigurationSources
                    .Select(static source => new ConfigurationSourceDocument
                    {
                        Name = source.Name,
                        Kind = source.Kind,
                    })
                    .ToArray(),
                Database = new DatabaseDocument
                {
                    Configured = database.IsConfigured,
                    Required = database.Required,
                    Target = ConnectionStringDescriptor.Describe(database.ConnectionString),
                    ConnectTimeoutSeconds = database.ConnectTimeoutSeconds,
                },
            },
            Probes = report.Probes
                .Select(static probe => new ProbeDocument
                {
                    Name = probe.Name,
                    Status = probe.Status switch
                    {
                        ProbeStatus.Healthy => "healthy",
                        ProbeStatus.Skipped => "skipped",
                        _ => "unhealthy",
                    },
                    Detail = probe.Detail,
                    DurationMs = Math.Round(probe.Duration.TotalMilliseconds, 3),
                })
                .ToArray(),
            Errors = [],
        };
    }

    private static async Task WriteHumanReportAsync(
        DiagnosticsDocument document,
        DiagnosticReport report,
        TextWriter stderr,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await stdout.WriteLineAsync($"status      : {document.Status}").ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"environment : {report.Runtime.EnvironmentName} (instance {report.Runtime.InstanceName})")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"runtime     : {report.Runtime.FrameworkDescription} on {report.Runtime.OsDescription}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"container   : {report.Runtime.IsContainer}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"pid / uptime: {report.Runtime.ProcessId} / {report.Runtime.Uptime?.ToString() ?? "unknown"}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"database    : {document.Configuration.Database.Target} (required: {document.Configuration.Database.Required})")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync(
                $"sources     : {string.Join(", ", report.ConfigurationSources.Select(static source => source.Name))}")
            .ConfigureAwait(false);

        foreach (var probe in document.Probes)
        {
            await stdout
                .WriteLineAsync($"probe {probe.Name,-10}: {probe.Status} ({probe.DurationMs:0.###} ms) {probe.Detail}")
                .ConfigureAwait(false);
        }

        await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (report.Status == HealthStatus.Unhealthy)
        {
            await stderr.WriteLineAsync("error: a configured dependency is unavailable.").ConfigureAwait(false);
        }
    }

    private static async Task WriteDocumentAsync(
        DiagnosticsDocument document,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await stdout.WriteLineAsync(json).ConfigureAwait(false);
        await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}