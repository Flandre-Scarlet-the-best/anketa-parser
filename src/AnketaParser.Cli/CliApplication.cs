using System.Text.Json;
using System.Text.Json.Serialization;

using AnketaParser.Cli;
using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;
using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace AnketaParser.Cli;

/// <summary>
/// Логика CLI агента и администратора. Отделена от <c>Program</c> для проверки потоков и кодов
/// завершения в тестах.
/// </summary>
public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Выполняет один вызов CLI.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="stdout">Поток машинного отчёта.</param>
    /// <param name="stderr">Поток человекочитаемой диагностики.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <param name="contentRootPath">Каталог настроек, переопределяемый в тестах.</param>
    /// <returns>Код завершения процесса.</returns>
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
        if (args is ["storage", ..])
            return await StorageCommands.RunAsync(args, stdout, stderr, contentRootPath, cancellationToken).ConfigureAwait(false);
        if (args is ["source", ..])
            return await SourceCommands.RunAsync(args, stdout, stderr, contentRootPath, cancellationToken).ConfigureAwait(false);
        if (args is ["llm", ..])
            return await LlmCommands.RunAsync(args, stdout, stderr, contentRootPath, cancellationToken).ConfigureAwait(false);

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
                try
                {
                    return await RunDoctorAsync(invocation, info, stdout, stderr, contentRootPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await stderr.WriteLineAsync("Диагностика остановлена.").ConfigureAwait(false);
                    return ExitCodes.UnexpectedError;
                }

            default:
                await stderr.WriteLineAsync($"Неподдерживаемая команда '{invocation.Command}'.").ConfigureAwait(false);
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
        var message = invocation.Error ?? "Неверные аргументы командной строки.";
        await stderr.WriteLineAsync($"Ошибка: {message}").ConfigureAwait(false);
        await stderr
            .WriteLineAsync($"Список команд и параметров: {HelpText.LaunchCommand} --help (из каталога CLI).")
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
            schemaVersion = DiagnosticsDocument.CurrentSchemaVersion,
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
        IReadOnlyList<ConfigurationSourceInfo> sources = [];

        try
        {
            var configuration = new ConfigurationBuilder();
            sources = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(
                () => AnketaParserConfiguration.Apply(configuration, contentRoot, environmentName));
            var configurationRoot = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(
                configuration.Build);
            using var configurationLifetime = configurationRoot as IDisposable;

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
                // Логи CLI не должны попадать в stdout машинного отчёта.
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

            provider.ValidateSettings();
            var databaseSettings = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            var diagnosticsSettings = provider.GetRequiredService<IOptions<DiagnosticsSettings>>().Value;

            var runner = provider.GetRequiredService<DiagnosticsRunner>();
            DiagnosticReport report;
            try
            {
                report = await runner
                    .RunAsync(sources, TimeSpan.FromSeconds(diagnosticsSettings.ProbeTimeoutSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await WriteCancelledDoctorAsync(
                    info, sources, databaseSettings, invocation.IsMachineReadable, stdout, stderr).ConfigureAwait(false);
            }

            var exitCode = report.Status == HealthStatus.Unhealthy
                ? ExitCodes.DependencyUnavailable
                : ExitCodes.Success;

            var document = CreateDocument(info, report, databaseSettings, exitCode);

            if (cancellationToken.IsCancellationRequested)
            {
                return await WriteCancelledDoctorAsync(
                    info, sources, databaseSettings, invocation.IsMachineReadable, stdout, stderr).ConfigureAwait(false);
            }

            if (invocation.IsMachineReadable)
            {
                await WriteDocumentAsync(document, stdout, CancellationToken.None).ConfigureAwait(false);
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
            await stderr.WriteLineAsync("Ошибка: конфигурация некорректна.").ConfigureAwait(false);
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

                await WriteDocumentAsync(document, stdout, CancellationToken.None).ConfigureAwait(false);
            }

            return exitCode;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr
                .WriteLineAsync($"Ошибка: внутренний сбой ({exception.GetType().Name}).")
                .ConfigureAwait(false);
            return ExitCodes.UnexpectedError;
        }
    }

    private static async Task<int> WriteCancelledDoctorAsync(
        ApplicationInfo info,
        IReadOnlyList<ConfigurationSourceInfo> sources,
        DatabaseSettings databaseSettings,
        bool machineReadable,
        TextWriter stdout,
        TextWriter stderr)
    {
        const int exitCode = ExitCodes.UnexpectedError;
        const string message = "Диагностика остановлена.";
        await stderr.WriteLineAsync(message).ConfigureAwait(false);
        if (machineReadable)
        {
            var document = new DiagnosticsDocument
            {
                Tool = info.ProductName,
                ToolVersion = info.Version,
                Command = "doctor",
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Status = "unhealthy",
                ExitCode = exitCode,
                Configuration = new ConfigurationDocument
                {
                    Valid = true,
                    Sources = sources.Select(static source => new ConfigurationSourceDocument
                    {
                        Name = source.Name,
                        Kind = source.Kind,
                    }).ToArray(),
                    Database = new DatabaseDocument
                    {
                        Configured = databaseSettings.IsConfigured,
                        Required = databaseSettings.Required,
                        Target = ConnectionStringDescriptor.Describe(databaseSettings.ConnectionString),
                        ConnectTimeoutSeconds = databaseSettings.ConnectTimeoutSeconds,
                    },
                },
                Probes = [],
                Errors = [message],
            };
            await WriteDocumentAsync(document, stdout, CancellationToken.None).ConfigureAwait(false);
        }

        return exitCode;
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
        await stdout.WriteLineAsync($"статус      : {document.Status}").ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"окружение   : {report.Runtime.EnvironmentName} (экземпляр {report.Runtime.InstanceName})")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"среда       : {report.Runtime.FrameworkDescription} в {report.Runtime.OsDescription}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"контейнер   : {report.Runtime.IsContainer}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"PID / время : {report.Runtime.ProcessId} / {report.Runtime.Uptime?.ToString() ?? "неизвестно"}")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync($"база данных : {document.Configuration.Database.Target} (обязательна: {document.Configuration.Database.Required})")
            .ConfigureAwait(false);
        await stdout
            .WriteLineAsync(
                $"источники   : {string.Join(", ", report.ConfigurationSources.Select(static source => source.Name))}")
            .ConfigureAwait(false);

        foreach (var probe in document.Probes)
        {
            await stdout
                .WriteLineAsync($"проверка {probe.Name,-10}: {probe.Status} ({probe.DurationMs:0.###} мс) {probe.Detail}")
                .ConfigureAwait(false);
        }

        await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (report.Status == HealthStatus.Unhealthy)
        {
            await stderr.WriteLineAsync("Ошибка: настроенная зависимость недоступна.").ConfigureAwait(false);
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