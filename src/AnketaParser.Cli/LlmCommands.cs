using System.Text.Json;
using System.Text.Json.Serialization;

using AnketaParser.Core;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Llm;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Cli;

internal static class LlmCommands
{
    private const string Usage = """
        Команды llm (вывод JSON):
          llm config                              Проверить активный профиль без вызова модели.
          llm status                              Прочитать очередь и статистику попыток.
          llm decision <revision-id>               Прочитать решения и причины по ревизии.
          llm reprocess <revision-id> [--retry-failed]
                                                  Поставить одну ревизию для активной версии.
          llm run <revision-id>                    Обработать одно доступное задание ревизии.
          llm worker                              Обрабатывать очередь до остановки процесса.
          llm help                                Показать справку.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? contentRoot, CancellationToken ct)
    {
        var arguments = args[1..];
        if (arguments.LastOrDefault() == "--json")
            arguments = arguments[..^1];
        if (arguments is ["help" or "--help" or "-h"])
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        var revisionId = Guid.Empty;
        var valid = arguments switch
        {
            ["config" or "status" or "worker"] => true,
            ["decision" or "run" or "reprocess", _] or ["reprocess", _, "--retry-failed"] =>
                Guid.TryParseExact(arguments[1], "D", out revisionId) && revisionId != Guid.Empty,
            _ => false,
        };
        var command = valid ? arguments[0] : "unknown";
        if (!valid)
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return await WriteAsync(stdout, command, new("invalid_usage", ExitCodes.InvalidUsage)).ConfigureAwait(false);
        }

        CommandResult result;
        try
        {
            result = await ExecuteAsync(command, revisionId, arguments.Length == 3, contentRoot, stderr, ct).ConfigureAwait(false);
        }
        catch (LlmConfigurationException error)
        {
            result = new(error.Category, ExitCodes.InvalidConfiguration);
        }
        catch (OptionsValidationException)
        {
            result = new("invalid_configuration", ExitCodes.InvalidConfiguration);
        }
        catch (LlmFailureException error)
        {
            result = new(error.Category, ExitCodes.DependencyUnavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = command == "worker" ? new("stopped", ExitCodes.Success) : new("cancelled", ExitCodes.UnexpectedError);
        }
        catch (Exception error) when (IsDatabaseFailure(error))
        {
            result = new("database_unavailable", ExitCodes.DependencyUnavailable);
        }
        catch (Exception)
        {
            result = new("internal_error", ExitCodes.UnexpectedError);
        }

        if (result.ExitCode != ExitCodes.Success)
            await stderr.WriteLineAsync("Операция LLM завершилась отказом. Категория указана в JSON.").ConfigureAwait(false);
        return await WriteAsync(stdout, command, result).ConfigureAwait(false);
    }

    private static async Task<CommandResult> ExecuteAsync(string command, Guid revisionId, bool retryFailed,
        string? contentRoot, TextWriter stderr, CancellationToken ct)
    {
        var environment = AnketaParserConfiguration.ResolveEnvironmentName();
        var builder = new ConfigurationBuilder();
        var sources = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(
            () => AnketaParserConfiguration.Apply(builder, contentRoot ?? AppContext.BaseDirectory, environment));
        var configuration = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(builder.Build);
        using var configurationLifetime = configuration as IDisposable;
        LlmSettings? settings = null;
        string? configurationFailure = null;
        if (command != "decision")
        {
            try
            {
                settings = await LlmConfiguration.LoadAsync(configuration, Environment.CurrentDirectory, ct).ConfigureAwait(false);
            }
            catch (LlmConfigurationException error) when (command == "status")
            {
                configurationFailure = error.Category;
            }
        }
        if (command == "config")
            return new(settings is null ? "disabled" : "ok", ExitCodes.Success, Describe(settings));
        if (settings is null && command is "run" or "reprocess" or "worker")
            return new("disabled", command == "worker" ? ExitCodes.Success : ExitCodes.InvalidConfiguration);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnketaParserInfrastructure(configuration, environment, sources);
        await using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
        if (!storage.IsConfigured)
            return new("database_not_configured", ExitCodes.InvalidConfiguration);
        var options = ProductDbContext.BuildOptions(storage.ConnectionString);
        await using (var database = new ProductDbContext(options))
        {
            if (await SchemaMaintenance.StatusAsync(database, ct).ConfigureAwait(false) != "ready")
                return new("schema_unavailable", ExitCodes.DependencyUnavailable);

            var reports = new LlmReports(database);
            switch (command)
            {
                case "status":
                    var status = await reports.StatusAsync(settings, ct).ConfigureAwait(false);
                    return new("ok", ExitCodes.Success, new
                    {
                        Configuration = Describe(settings),
                        ConfigurationFailure = configurationFailure,
                        Status = status,
                    });
                case "decision":
                    var decision = await reports.DecisionAsync(revisionId, ct).ConfigureAwait(false);
                    return new(decision is null ? "revision_not_found" : "ok", ExitCodes.Success, decision);
                case "reprocess":
                    var jobId = await reports.ReprocessAsync(revisionId, settings!, retryFailed, ct).ConfigureAwait(false);
                    return jobId is null
                        ? new("revision_ineligible", ExitCodes.DependencyUnavailable)
                        : new("ok", ExitCodes.Success, new
                        {
                            JobId = jobId,
                            RevisionId = revisionId,
                            settings!.PipelineVersion,
                            LlmAnalysisWorker.MaxRequestsPerAttempt
                        });
            }
        }

        using var client = settings!.Engine == LlmEngine.Native ? null : OpenAiLlmEngine.CreateClient();
        using var native = client is null ? await NativeLlmEngine.LoadAsync(settings, ct).ConfigureAwait(false) : null;
        ILlmEngine engine = native is null ? new OpenAiLlmEngine(settings, client!) : native;
        var worker = new LlmAnalysisWorker(options, settings, engine);
        if (command == "run")
        {
            var outcome = await worker.ProcessNextAsync(revisionId, ct).ConfigureAwait(false);
            return new(outcome.Status, RunExitCode(outcome.Status),
                new { outcome.RevisionId, outcome.Attempt, settings.PipelineVersion });
        }

        await stderr.WriteLineAsync($"Обработчик LLM запущен ({LlmConfiguration.EngineName(settings.Engine)}, {settings.PipelineVersion}).")
            .ConfigureAwait(false);
        await Parallel.ForAsync(0, settings.Concurrency, ct, async (_, stop) =>
        {
            await ConsumeAsync(worker, stderr, stop).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return new("stopped", ExitCodes.Success);
    }

    private static async Task ConsumeAsync(LlmAnalysisWorker worker, TextWriter stderr, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LlmWorkOutcome outcome;
            try
            {
                outcome = await worker.ProcessNextAsync(null, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (IsDatabaseFailure(error))
            {
                outcome = new("database_unavailable");
            }

            if (outcome.Status == "idle")
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            else if (outcome.Status == "budget_exhausted")
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            else if (!IsSuccess(outcome.Status) && outcome.Status != "cancelled")
            {
                await stderr.WriteLineAsync($"Попытка LLM завершилась отказом ({outcome.Status}).").ConfigureAwait(false);
                if (IsFatalRouteFailure(outcome.Status))
                    throw new LlmFailureException(outcome.Status);
                if (outcome.Status is "database_unavailable" or "network_unavailable" or "timeout" or "rate_limited" or "provider_unavailable")
                {
                    var cooldown = TimeSpan.FromSeconds(Math.Clamp(outcome.RetryAfter?.TotalSeconds ?? 30, 10, 86400));
                    await Task.Delay(cooldown, ct).ConfigureAwait(false);
                }
            }
        }
    }

    internal static int RunExitCode(string status) => status == "cancelled" ? ExitCodes.UnexpectedError :
        IsSuccess(status) ? ExitCodes.Success : ExitCodes.DependencyUnavailable;

    internal static bool IsFatalRouteFailure(string status) => status is
        "provider_auth" or "provider_payment_required" or "unsupported_request" or "model_or_endpoint_unavailable" or
        "endpoint_method_not_allowed" or "secret_unavailable" or "native_inference_failed";

    private static bool IsSuccess(string status) =>
        status is "idle" or SemanticContract.Profile or SemanticContract.NotProfile or SemanticContract.Uncertain;

    private static bool IsDatabaseFailure(Exception error) => error is NpgsqlException or DbUpdateException or TimeoutException or
        InvalidOperationException { InnerException: NpgsqlException or TimeoutException };

    private static object? Describe(LlmSettings? settings) => settings is null ? null : new
    {
        Profile = settings.Name,
        Engine = LlmConfiguration.EngineName(settings.Engine),
        settings.Model,
        settings.PipelineVersion,
        settings.Concurrency,
        settings.TimeoutSeconds,
        settings.MaxOutputTokens,
        settings.MaxInputChars,
        settings.DailyRequestLimit,
    };

    private static async Task<int> WriteAsync(TextWriter output, string command, CommandResult result)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Command = "llm " + command,
            result.Status,
            result.ExitCode,
            result.Result,
        }, JsonOptions)).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        return result.ExitCode;
    }

    private sealed record CommandResult(string Status, int ExitCode, object? Result = null);
}