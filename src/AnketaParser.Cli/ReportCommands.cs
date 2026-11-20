using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Reporting;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Cli;

/// <summary>
/// Команда отчёта: читает существующую схему продукта и пишет один автономный HTML-файл.
/// Хранилище не изменяется, миграции не применяются, модель не вызывается.
/// </summary>
internal static class ReportCommands
{
    private const int CurrentSchemaVersion = 1;

    private const string Usage = """
        Команды report (автономный HTML-отчёт, машинный отчёт в stdout):
          report html --output <файл.html> [--source-key <ключ>] [--run-metadata <файл.json>] [--fixture]
              Построить отчёт по одному зафиксированному снимку источника: снимок, первичный
              фильтр, решения модели. Метаданные движка и параметров запуска берутся из
              необязательного JSON; путь к нему передаётся параметром --run-metadata.
          report help
              Показать справку.

        Параметры:
          --output <файл>        Обязательный путь к создаваемому HTML-файлу.
          --source-key <ключ>    Ключ источника (Source.Key). По умолчанию — адрес треда из
                                 настройки Dvach:ThreadUrl.
          --run-metadata <файл>  Необязательный JSON с описанием движка и параметров запуска.
          --fixture              Использовать ключ фикстуры вместо адреса треда.
          --json                 Принимается и не меняет вывод: машинный отчёт всегда в stdout.

        Коды завершения: 0 — отчёт построен, 2 — аргументы, 3 — конфигурация или входные данные,
        4 — снимок недоказуем либо хранилище недоступно. HTML пишется только при успехе.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Выполняет команду отчёта.</summary>
    /// <param name="args">Полные аргументы командной строки, включая <c>report</c>.</param>
    /// <param name="stdout">Поток машинного отчёта.</param>
    /// <param name="stderr">Поток человекочитаемой диагностики.</param>
    /// <param name="contentRoot">Каталог настроек.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Код завершения процесса.</returns>
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, string? contentRoot, CancellationToken cancellationToken)
    {
        var arguments = args.Skip(1).ToArray();
        if (arguments.LastOrDefault() == "--json")
        {
            arguments = arguments[..^1];
        }

        if (arguments is ["help" or "--help" or "-h"])
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (!TryParse(arguments, out var options, out var problem))
        {
            await TryWriteAsync(stderr, problem).ConfigureAwait(false);
            await TryWriteAsync(stderr, Usage).ConfigureAwait(false);
            return await WriteAsync(stdout, "html", "invalid_usage", ExitCodes.InvalidUsage).ConfigureAwait(false);
        }

        var information = ApplicationInfo.ForEntryAssembly();
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
            // Та же конфигурация и те же типизированные проверки, что у остальных команд.
            provider.ValidateSettings();

            var database = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            var dvach = provider.GetRequiredService<IOptions<DvachSettings>>().Value;
            var sourceKey = options.SourceKey ?? ResolveSourceKey(dvach.ThreadUrl, options.Fixture);
            if (!database.IsConfigured || sourceKey is null)
            {
                status = !database.IsConfigured ? "not_configured" : "source_key_required";
                code = ExitCodes.InvalidConfiguration;
                await TryWriteAsync(stderr, !database.IsConfigured
                    ? "Отчёт не построен: соединение с хранилищем не настроено."
                    : "Отчёт не построен: ключ источника не задан и адрес треда не настроен в Dvach:ThreadUrl.").ConfigureAwait(false);
            }
            else
            {
                var runMetadata = options.RunMetadataPath is null
                    ? null
                    : await ReportRunMetadataReader.ReadAsync(options.RunMetadataPath, cancellationToken).ConfigureAwait(false);
                await using var scope = provider.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
                var snapshot = await new ReportQueries(context).LoadAsync(sourceKey, cancellationToken).ConfigureAwait(false);
                var document = ReportComposer.Build(snapshot, runMetadata, information.Version, DateTimeOffset.UtcNow);
                var html = ReportHtmlRenderer.Render(document);
                await WriteHtmlAsync(options.OutputPath, html, cancellationToken).ConfigureAwait(false);

                status = "ok";
                code = ExitCodes.Success;
                result = Describe(options.OutputPath, document, html, sourceKey);
                await TryWriteAsync(stderr, string.Create(CultureInfo.InvariantCulture,
                    $"Отчёт записан: {Path.GetFullPath(options.OutputPath)} (постов в снимке: {document.Posts.Count}).")).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = "cancelled";
            code = ExitCodes.UnexpectedError;
        }
        catch (ReportDiagnosticException error)
        {
            // Причина отказа указывается фактическими значениями: отчёт по смешанному снимку не строится.
            status = error.Category;
            code = error.Category is "run_metadata_unreadable" or "run_metadata_invalid" or "output_unwritable"
                ? ExitCodes.InvalidConfiguration
                : ExitCodes.DependencyUnavailable;
            result = new
            {
                category = error.Category,
                facts = error.Facts.Select(fact => new { fact.Name, fact.Value }).ToArray(),
            };
            await TryWriteAsync(stderr, DescribeDiagnostic("Отчёт не построен", error)).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState is "42P01" or "42703" or "3F000")
        {
            status = "schema_unavailable";
            code = ExitCodes.DependencyUnavailable;
            await TryWriteAsync(stderr, "Отчёт не построен: схема продукта в хранилище отсутствует или неполна.").ConfigureAwait(false);
        }
        catch (Exception error) when (error is DbException or DependencyUnavailableException)
        {
            status = "database_unavailable";
            code = ExitCodes.DependencyUnavailable;
            await TryWriteAsync(stderr, "Отчёт не построен: хранилище недоступно.").ConfigureAwait(false);
        }
        catch (Exception error) when (error is OptionsValidationException or InvalidDataException or FormatException or ArgumentException)
        {
            status = "invalid_configuration";
            code = ExitCodes.InvalidConfiguration;
            await TryWriteAsync(stderr, "Отчёт не построен: конфигурация некорректна.").ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            status = "internal_error";
            code = ExitCodes.UnexpectedError;
            await TryWriteAsync(stderr, $"Отчёт не построен: внутренний сбой ({error.GetType().Name}).").ConfigureAwait(false);
        }

        return await WriteAsync(stdout, "html", status, code, result).ConfigureAwait(false);
    }

    /// <summary>Ключ источника: явный аргумент либо адрес треда из настройки.</summary>
    private static string? ResolveSourceKey(string? threadUrl, bool fixture)
    {
        var thread = DvachThread.Parse(threadUrl);
        return thread is null ? null : fixture ? thread.FixtureKey : thread.SourceKey;
    }

    private static bool TryParse(string[] arguments, out ReportOptions options, out string problem)
    {
        options = new ReportOptions();
        problem = string.Empty;
        if (arguments.Length == 0 || arguments[0] != "html")
        {
            problem = "Ошибка: неизвестная операция report.";
            return false;
        }

        string? output = null;
        string? sourceKey = null;
        string? runMetadata = null;
        var fixture = false;
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            string? value = index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-')
                ? arguments[index + 1]
                : null;
            switch (argument)
            {
                case "--output" when value is not null:
                    output = value;
                    index++;
                    break;
                case "--source-key" when value is not null:
                    sourceKey = value;
                    index++;
                    break;
                case "--run-metadata" when value is not null:
                    runMetadata = value;
                    index++;
                    break;
                case "--fixture":
                    fixture = true;
                    break;
                case "--json":
                    break;
                default:
                    problem = $"Ошибка: неизвестный или неполный параметр '{argument}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            problem = "Ошибка: не задан обязательный параметр --output.";
            return false;
        }

        if (runMetadata is not null && SamePath(output, runMetadata))
        {
            // Результат заменил бы переданные метаданные, которые уже прочитаны для отчёта.
            problem = "Ошибка: --output и --run-metadata указывают на один файл: исходные метаданные были бы перезаписаны.";
            return false;
        }

        options = new ReportOptions(output, sourceKey, runMetadata, fixture);
        return true;
    }

    /// <summary>
    /// Пути совпадают после развёртывания в абсолютную форму и разрешения ссылок:
    /// запись через ссылку на каталог не должна заменить исходный файл метаданных.
    /// </summary>
    private static bool SamePath(string first, string second)
    {
        try
        {
            // APFS по умолчанию регистронезависим, как и файловые системы Windows:
            // сравниваем имена так же, как их сравнивает файловая система.
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(ResolvedPath(first), ResolvedPath(second), comparison);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    /// <summary>Абсолютный путь с разрешёнными ссылками самого файла и его каталогов.</summary>
    private static string ResolvedPath(string path)
    {
        var full = Path.GetFullPath(path);
        var file = new FileInfo(full);
        if (file.Exists && file.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            full = target.FullName;
        var directory = Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(directory)
            ? full
            : Path.Combine(ResolvedDirectory(directory), Path.GetFileName(full));
    }

    /// <summary>Каталог с разрешёнными ссылками во всех существующих предках.</summary>
    private static string ResolvedDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        // Корень и несуществующий каталог разрешать нечего: ссылки в них отсутствуют по контракту.
        if (!info.Exists || info.Parent is null)
            return directory;
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory;
        var parent = Path.GetDirectoryName(resolved);
        return string.IsNullOrEmpty(parent) || string.Equals(parent, resolved, StringComparison.Ordinal)
            ? resolved
            : Path.Combine(ResolvedDirectory(parent), Path.GetFileName(resolved));
    }

    internal static async Task WriteHtmlAsync(string path, string html, CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var candidate = Path.Combine(directory!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                temporary = candidate;
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(html.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException)
        {
            throw new ReportDiagnosticException("output_unwritable", [new ReportDiagnosticFact("path", path)]);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static object Describe(string outputPath, ReportDocument document, string html, string sourceKey)
    {
        var posts = document.Posts;
        long Stage(string stage) => posts.Count(post => post.Stage == stage);
        return new
        {
            output = Path.GetFullPath(outputPath),
            htmlBytes = Encoding.UTF8.GetByteCount(html),
            toolVersion = document.ToolVersion,
            generatedAt = document.GeneratedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            source = new { key = sourceKey, url = document.Snapshot.SourceUrl },
            snapshot = new
            {
                document.Snapshot.StartedAt,
                document.Snapshot.FinishedAt,
                document.Snapshot.Received,
                document.Snapshot.Applied,
                document.Snapshot.IdsInWindow,
                document.Snapshot.PostsOutsideWindow,
                posts = posts.Count,
            },
            counts = new
            {
                profile = Stage(ReportComposer.StageProfile),
                notProfile = Stage(ReportComposer.StageNotProfile),
                uncertain = Stage(ReportComposer.StageUncertain),
                pending = Stage(ReportComposer.StagePending),
                running = Stage(ReportComposer.StageRunning),
                failed = Stage(ReportComposer.StageFailed),
                noJob = Stage(ReportComposer.StageNoJob),
                staleOnly = Stage(ReportComposer.StageStaleOnly),
                notAdmitted = Stage(ReportComposer.StageNotAdmitted),
                moderationExcluded = posts.Count(post => post.ModerationExcluded),
            },
            notices = document.Notices.Select(notice => new { notice.Code, notice.Level, notice.Text }).ToArray(),
        };
    }

    private static string DescribeDiagnostic(string prefix, ReportDiagnosticException error) =>
        error.Facts.Count == 0
            ? $"{prefix}: {error.Category}."
            : string.Create(CultureInfo.InvariantCulture,
                $"{prefix}: {error.Category} ({string.Join(", ", error.Facts.Select(fact => fact.Name + "=" + fact.Value))}).");

    /// <summary>Пояснение или отчёт об ошибке не должны отменять машинный отчёт в stdout.</summary>
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

    private static async Task<int> WriteAsync(
        TextWriter stdout, string command, string status, int exitCode, object? result = null)
    {
        await stdout.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = CurrentSchemaVersion,
            command = "report " + command,
            status,
            exitCode,
            result,
        }, JsonOptions)).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
        return exitCode;
    }

    private sealed record ReportOptions(
        string OutputPath, string? SourceKey, string? RunMetadataPath, bool Fixture)
    {
        internal ReportOptions()
            : this(string.Empty, null, null, false)
        {
        }
    }
}