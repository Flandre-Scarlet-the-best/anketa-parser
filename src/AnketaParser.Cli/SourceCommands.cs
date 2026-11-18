using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Sources;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AnketaParser.Cli;

internal static class SourceCommands
{
    private const string Usage = """
        Команды source (вывод JSON):
          source scan [--fixture <файл.json>]  Выполнить один опрос и сохранить результат.
          source status [--fixture]            Прочитать сохранённое состояние опроса.
          source post <номер> [--fixture]      Прочитать решение и состояние задания.
          source queue [--fixture]            Посчитать задания по состоянию и актуальности.
          source help                         Показать справку.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? contentRoot, CancellationToken ct)
    {
        if (args is ["source", "help" or "--help" or "-h"])
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        var arguments = args.Skip(1).ToArray();
        if (arguments.LastOrDefault() == "--json")
            arguments = arguments[..^1];
        long postNumber = 0;
        var valid = arguments switch
        {
            ["scan"] or ["status" or "queue"] or ["status" or "queue", "--fixture"] => true,
            ["scan", "--fixture", var path] => !string.IsNullOrWhiteSpace(path) && !path.StartsWith('-'),
            ["post", var number] => ParseNumber(number, out postNumber),
            ["post", var number, "--fixture"] => ParseNumber(number, out postNumber),
            _ => false,
        };

        var command = valid ? arguments[0] : "unknown";
        if (!valid)
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return await WriteAsync(stdout, command, "invalid_usage", ExitCodes.InvalidUsage).ConfigureAwait(false);
        }

        try
        {
            var environment = AnketaParserConfiguration.ResolveEnvironmentName();
            var builder = new ConfigurationBuilder();
            var sources = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(
                () => AnketaParserConfiguration.Apply(builder, contentRoot ?? AppContext.BaseDirectory, environment));
            var configuration = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(builder.Build);
            using var configurationLifetime = configuration as IDisposable;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAnketaParserInfrastructure(configuration, environment, sources);
            await using var provider = services.BuildServiceProvider();
            // Команды источника используют тот же конвейер и те же типизированные проверки.
            provider.ValidateSettings();
            var ingestion = provider.GetRequiredService<DvachIngestion>();
            if (!ingestion.IsConfigured)
                return await WriteAsync(stdout, command, "not_configured", ExitCodes.InvalidConfiguration).ConfigureAwait(false);

            switch (command)
            {
                case "scan":
                    var outcome = await ingestion.ScanAsync(arguments.Length == 3 ? arguments[2] : null, ct).ConfigureAwait(false);
                    var code = outcome.Status == "complete" ? ExitCodes.Success : ExitCodes.DependencyUnavailable;
                    return await WriteAsync(stdout, command, outcome.Status, code, outcome).ConfigureAwait(false);
                case "status":
                    var state = await ingestion.StatusAsync(arguments.Length == 2, ct).ConfigureAwait(false);
                    return await WriteAsync(stdout, command, state.Status, ExitCodes.Success, state).ConfigureAwait(false);
                case "post":
                    var report = await ingestion.PostAsync(arguments.Length == 3, postNumber, ct).ConfigureAwait(false);
                    var reportCode = report.Status == "ok" ? ExitCodes.Success : ExitCodes.DependencyUnavailable;
                    return await WriteAsync(stdout, command, report.Status, reportCode, report).ConfigureAwait(false);
                default:
                    var jobs = await ingestion.QueueAsync(arguments.Length == 2, ct).ConfigureAwait(false);
                    return await WriteAsync(stdout, command, "ok", ExitCodes.Success, jobs).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await WriteAsync(stdout, command, "cancelled", ExitCodes.UnexpectedError).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var (status, code) = error switch
            {
                DependencyUnavailableException { Message: "schema_unavailable" } => ("schema_unavailable", ExitCodes.DependencyUnavailable),
                DependencyUnavailableException => ("database_unavailable", ExitCodes.DependencyUnavailable),
                OptionsValidationException or InvalidDataException or FormatException or ArgumentException =>
                    ("invalid_configuration", ExitCodes.InvalidConfiguration),
                _ => ("internal_error", ExitCodes.UnexpectedError),
            };
            await stderr.WriteLineAsync("Операция источника не выполнена. Категория указана в отчёте.").ConfigureAwait(false);
            return await WriteAsync(stdout, command, status, code).ConfigureAwait(false);
        }
    }

    private static bool ParseNumber(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static async Task<int> WriteAsync(TextWriter stdout, string command, string status, int exitCode, object? result = null)
    {
        await stdout.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = "source " + command,
            status,
            exitCode,
            result,
        }, JsonOptions)).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
        return exitCode;
    }
}