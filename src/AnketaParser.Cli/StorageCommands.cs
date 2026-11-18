using System.Text.Json;

using AnketaParser.Core;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnketaParser.Cli;

internal static partial class StorageCommands
{
    private const string Usage = "Команды storage: help, provision, migrate, schema, backup, backup-status, backup-list, backup-rotate, backup-verify <имя>, restore-empty <имя>, backup-daemon.";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? contentRoot, CancellationToken ct)
    {
        if (args is ["storage", "help" or "--help" or "-h"])
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        if (args is not (["storage", "provision" or "migrate" or "schema" or "backup" or "backup-status" or "backup-list" or "backup-rotate" or "backup-daemon"] or
            ["storage", "backup-verify" or "restore-empty", _]))
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.InvalidUsage;
        }
        try
        {
            if (args is ["storage", "backup-verify" or "restore-empty", var name] && !BackupService.IsArchiveName(name))
            {
                await stderr.WriteLineAsync("Требуется имя архива .dump без пути.").ConfigureAwait(false);
                return ExitCodes.InvalidUsage;
            }
            var builder = new ConfigurationBuilder();
            var environment = AnketaParserConfiguration.ResolveEnvironmentName();
            var sources = AnketaParserConfiguration.Apply(builder, contentRoot ?? AppContext.BaseDirectory, environment);
            var config = builder.Build();
            using var configLifetime = config as IDisposable;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAnketaParserInfrastructure(config, environment, sources);
            await using var provider = services.BuildServiceProvider();
            provider.ValidateSettings();
            var database = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            var settings = provider.GetRequiredService<IOptions<BackupSettings>>().Value;
            switch (args)
            {
                case ["storage", "provision"]:
                    await SchemaMaintenance.ProvisionAsync(database.ConnectionString,
                        config["Provision:MigrationPassword"]!, config["Provision:RuntimePassword"]!, config["Provision:BackupPassword"]!, ct).ConfigureAwait(false);
                    break;
                case ["storage", "migrate"]:
                case ["storage", "schema"]:
                    await using (var db = ProductDbContext.Create(database.ConnectionString))
                    {
                        if (args[1] == "migrate") await SchemaMaintenance.MigrateAsync(db, ct).ConfigureAwait(false);
                        var status = await SchemaMaintenance.StatusAsync(db, ct).ConfigureAwait(false);
                        await stdout.WriteLineAsync(JsonSerializer.Serialize(new { schema = status })).ConfigureAwait(false);
                        return status == "ready" ? ExitCodes.Success : ExitCodes.DependencyUnavailable;
                    }
                case ["storage", "backup"]:
                    await stdout.WriteLineAsync(JsonSerializer.Serialize(new { archive = await new BackupService(settings).RunAsync(ct).ConfigureAwait(false) })).ConfigureAwait(false);
                    return ExitCodes.Success;
                case ["storage", "backup-status"]:
                    await stdout.WriteLineAsync(JsonSerializer.Serialize(new BackupService(settings).Status())).ConfigureAwait(false);
                    return ExitCodes.Success;
                case ["storage", "backup-list"]:
                    await stdout.WriteLineAsync(JsonSerializer.Serialize(new BackupService(settings).List())).ConfigureAwait(false);
                    return ExitCodes.Success;
                case ["storage", "backup-rotate"]:
                    new BackupService(settings).Rotate();
                    break;
                case ["storage", "backup-verify", var archive]:
                    await new BackupService(settings).VerifyAsync(archive, ct).ConfigureAwait(false);
                    break;
                case ["storage", "restore-empty", var archive]:
                    {
                        // Факт восстановления и запись служебного статуса сообщаются раздельно.
                        var outcome = await new BackupService(settings).RestoreAsync(archive, config["Restore:ConnectionString"]!, ct).ConfigureAwait(false);
                        await stdout.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            archive = outcome.Archive,
                            restore_verified = true,
                            status_record = outcome.StatusRecord switch
                            {
                                RestoreStatusRecord.Updated => "updated",
                                RestoreStatusRecord.NotCurrentArchive => "not_current_archive",
                                RestoreStatusRecord.RepairedFromCorrupt => "repaired_corrupt_status",
                                _ => "not_recorded",
                            },
                        })).ConfigureAwait(false);
                        if (outcome.StatusRecord == RestoreStatusRecord.RepairedFromCorrupt)
                            await stderr.WriteLineAsync("Служебный статус резервирования был повреждён и сохранён отдельно; восстановление подтверждено отметкой архива.").ConfigureAwait(false);
                        else if (outcome.StatusRecord == RestoreStatusRecord.NotRecorded)
                            await stderr.WriteLineAsync("Восстановление подтверждено отметкой архива, но служебный статус не обновлён; проверьте доступность каталога резервирования.").ConfigureAwait(false);
                        return ExitCodes.Success;
                    }
                case ["storage", "backup-daemon"]:
                    {
                        var service = new BackupService(settings);
                        using var loggerFactory = LoggerFactory.Create(logging =>
                            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
                        var logger = loggerFactory.CreateLogger("AnketaParser.Backup");
                        while (!ct.IsCancellationRequested)
                        {
                            try { await service.RunAsync(ct).ConfigureAwait(false); }
                            catch (Exception e) when (e is not OperationCanceledException) { LogBackupFailure(logger, e.GetType().Name); }
                            await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), ct).ConfigureAwait(false);
                        }
                        return ExitCodes.Success;
                    }
                default:
                    await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
                    return ExitCodes.InvalidUsage;
            }
            await stdout.WriteLineAsync("{\"status\":\"ok\"}").ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Операция хранения остановлена.").ConfigureAwait(false);
            return args is ["storage", "backup-daemon"] ? ExitCodes.Success : ExitCodes.UnexpectedError;
        }
        catch (RestoreTargetException exception)
        {
            await stderr.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitCodes.DependencyUnavailable;
        }
        catch (RestoreCompletionException exception)
        {
            // Данные уже восстановлены; сообщение объясняет, какая служебная запись не выполнена.
            await stderr.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitCodes.UnexpectedError;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await stderr.WriteLineAsync($"Операция хранения не выполнена ({exception.GetType().Name}); секреты и сообщения зависимостей скрыты.").ConfigureAwait(false);
            return exception is ArgumentException or OptionsValidationException ? ExitCodes.InvalidConfiguration : ExitCodes.DependencyUnavailable;
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Резервирование завершилось отказом ({FailureCategory}); проверьте backup-status.")]
    private static partial void LogBackupFailure(ILogger logger, string failureCategory);
}