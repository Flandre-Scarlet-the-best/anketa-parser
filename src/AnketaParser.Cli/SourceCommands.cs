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
        var arguments = args.Skip(1).ToArray();
        if (arguments.LastOrDefault() == "--json")
            arguments = arguments[..^1];
        // Справка остаётся текстовой и распознаётся после удаления флага вывода.
        if (arguments is ["help" or "--help" or "-h"])
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Success;
        }

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
            await TryWriteAsync(stderr, Usage).ConfigureAwait(false);
            return await WriteAsync(stdout, command, "invalid_usage", ExitCodes.InvalidUsage).ConfigureAwait(false);
        }

        // Результат формируется внутри try/catch, а документ в stdout пишется один раз:
        // отказ записи или сброса потока не добавляет второй JSON.
        string status;
        int code;
        object? result = null;
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
            {
                status = "not_configured";
                code = ExitCodes.InvalidConfiguration;
            }
            else
            {
                switch (command)
                {
                    case "scan":
                        var outcome = await ingestion.ScanAsync(arguments.Length == 3 ? arguments[2] : null, ct).ConfigureAwait(false);
                        status = outcome.Status;
                        code = outcome.Status == "complete" ? ExitCodes.Success : ExitCodes.DependencyUnavailable;
                        result = outcome;
                        break;
                    case "status":
                        var state = await ingestion.StatusAsync(arguments.Length == 2, ct).ConfigureAwait(false);
                        status = state.Status;
                        code = ExitCodes.Success;
                        result = state;
                        break;
                    case "post":
                        var report = await ingestion.PostAsync(arguments.Length == 3, postNumber, ct).ConfigureAwait(false);
                        status = report.Status;
                        // Выполненный запрос без поста — успешное чтение хранилища,
                        // а не недоступная обязательная зависимость.
                        code = report.Status is "ok" or "post_not_found" ? ExitCodes.Success : ExitCodes.DependencyUnavailable;
                        result = report;
                        break;
                    default:
                        result = await ingestion.QueueAsync(arguments.Length == 2, ct).ConfigureAwait(false);
                        status = "ok";
                        code = ExitCodes.Success;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
            code = ExitCodes.UnexpectedError;
            result = null;
        }
        catch (Exception error)
        {
            (status, code) = error switch
            {
                DependencyUnavailableException { Message: "schema_unavailable" } => ("schema_unavailable", ExitCodes.DependencyUnavailable),
                DependencyUnavailableException => ("database_unavailable", ExitCodes.DependencyUnavailable),
                OptionsValidationException or InvalidDataException or FormatException or ArgumentException =>
                    ("invalid_configuration", ExitCodes.InvalidConfiguration),
                _ => ("internal_error", ExitCodes.UnexpectedError),
            };
            // Отчёт об ошибке не содержит частичного результата операции.
            result = null;
            await TryWriteAsync(stderr, "Операция источника не выполнена. Категория указана в отчёте.").ConfigureAwait(false);
        }

        return await WriteAsync(stdout, command, status, code, result).ConfigureAwait(false);
    }

    private static bool ParseNumber(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    /// <summary>Пояснение в stderr не должно отменять сформированный отчёт в stdout.</summary>
    private static async Task TryWriteAsync(TextWriter writer, string text)
    {
        try
        {
            await writer.WriteLineAsync(text).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Отказ пояснения не меняет статус и код завершения команды.
        }
    }

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