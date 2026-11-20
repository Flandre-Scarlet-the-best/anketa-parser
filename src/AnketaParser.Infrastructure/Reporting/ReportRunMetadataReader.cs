using System.Globalization;
using System.Text.Json;

namespace AnketaParser.Infrastructure.Reporting;

/// <summary>
/// Читает необязательный JSON с метаданными движка и параметров запуска. Схема файла не задаётся
/// жёстко: известные сведения находятся по именам полей, полный список плоских значений
/// показывается рядом, чтобы источник каждого факта был виден в отчёте.
/// </summary>
public static class ReportRunMetadataReader
{
    private const int MaxItems = 200;
    private const int MaxNamed = 2_000;
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxValueChars = 300;
    private const int MaxSamplingItems = 12;

    /// <summary>Признаки имён полей, значения которых не попадают в отчёт.</summary>
    private static readonly string[] SensitiveNames =
        ["key", "token", "secret", "password", "passwd", "credential", "authorization", "auth", "cookie", "session", "bearer"];

    private const string Hidden = "скрыто";

    private static readonly string[] ModelNames = ["generalName", "modelName", "model.name", "name"];
    private static readonly string[] EngineNames = ["runtime", "backend", "engine"];
    private static readonly string[] EngineVersionNames = ["buildVersion", "engineVersion", "version"];
    private static readonly string[] QuantizationNames = ["quantization", "quantizationLabel"];
    private static readonly string[] HashNames = ["sha256", "modelSha256", "ggufSha256"];
    private static readonly string[] EndpointNames = ["baseUrl", "endpoint", "modelsUrl"];

    private static readonly string[] SamplingHints =
        ["temperature", "top_p", "top_k", "min_p", "repeat_penalty", "repeat_last_n", "seed", "n_ctx", "ctx", "max_output"];

    /// <summary>Группы имён, из которых собирается сводка распознанных полей.</summary>
    private static readonly string[][] FactNames = [ModelNames, EngineNames, EngineVersionNames, QuantizationNames, HashNames, EndpointNames];

    /// <summary>Читает файл метаданных запуска.</summary>
    /// <param name="path">Путь к JSON-файлу.</param>
    /// <param name="cancellationToken">Токен отмены чтения.</param>
    /// <returns>Метаданные запуска с полным списком плоских значений.</returns>
    /// <exception cref="ReportDiagnosticException">Файл недоступен или содержит некорректный JSON.</exception>
    public static async Task<ReportRunMetadataView> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new ReportDiagnosticException("run_metadata_unreadable", [new ReportDiagnosticFact("path", path)]);
        }

        if (length > MaxFileBytes)
        {
            // Файл читается целиком: предел защищает процесс отчёта от исчерпания памяти.
            throw new ReportDiagnosticException("run_metadata_too_large",
            [
                new ReportDiagnosticFact("path", path),
                new ReportDiagnosticFact("limit", MaxFileBytes.ToString(CultureInfo.InvariantCulture)),
            ]);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ReportDiagnosticException("run_metadata_unreadable", [new ReportDiagnosticFact("path", path)]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw new ReportDiagnosticException("run_metadata_invalid", [new ReportDiagnosticFact("path", path)]);
        }

        using (document)
        {
            // Обход идёт по всему файлу: длинный массив не должен скрывать распознаваемые
            // поля. Предел относится только к отображаемому списку плоских значений.
            List<ReportMetadataItem> displayed = [];
            List<ReportMetadataItem> named = [];
            var leaves = 0;
            try
            {
                Flatten(document.RootElement, string.Empty, displayed, named, ref leaves);
            }
            catch (InvalidOperationException)
            {
                // Непарный суррогат в строке файла не декодируется: файл непригоден.
                throw new ReportDiagnosticException("run_metadata_invalid", [new ReportDiagnosticFact("path", path)]);
            }

            return new ReportRunMetadataView(path, true, Summarize(named), displayed, leaves > MaxItems);
        }
    }

    private static List<ReportMetadataItem> Summarize(IReadOnlyList<ReportMetadataItem> all)
    {
        List<ReportMetadataItem> facts = [];
        Add(facts, all, "Модель", ModelNames);
        Add(facts, all, "Движок", EngineNames);
        Add(facts, all, "Версия движка", EngineVersionNames);
        Add(facts, all, "Квантование", QuantizationNames);
        Add(facts, all, "SHA-256 модели", HashNames);
        Add(facts, all, "Endpoint", EndpointNames);
        var sampling = all
            .Where(item => SamplingHints.Any(hint => item.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxSamplingItems)
            .Select(item => new ReportMetadataItem("Параметр запуска: " + item.Name, item.Value))
            .ToArray();
        facts.AddRange(sampling);
        return facts;
    }

    private static void Add(
        List<ReportMetadataItem> facts, IReadOnlyList<ReportMetadataItem> all, string label, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var match = all.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                item.Name.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;
            facts.Add(new ReportMetadataItem($"{label} ({match.Name})", match.Value));
            return;
        }
    }

    private static void Flatten(
        JsonElement element,
        string path,
        List<ReportMetadataItem> displayed,
        List<ReportMetadataItem> named,
        ref int leaves)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var component = property.Name.All(character => char.IsLetterOrDigit(character) || character == '_') &&
                        property.Name.Length > 0
                        ? (path.Length == 0 ? property.Name : "." + property.Name)
                        : "[" + JsonSerializer.Serialize(property.Name) + "]";
                    Flatten(property.Value, path + component, displayed, named, ref leaves);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), displayed, named, ref leaves);
                    index++;
                }

                break;
            case JsonValueKind.String:
                Collect(path, Shorten(element.GetString() ?? string.Empty), displayed, named, ref leaves);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                Collect(path, Shorten(element.GetRawText()), displayed, named, ref leaves);
                break;
            default:
                // null не добавляется: отсутствие значения не является фактом запуска.
                break;
        }
    }

    private static void Collect(
        string path, string value, List<ReportMetadataItem> displayed, List<ReportMetadataItem> named, ref int leaves)
    {
        leaves++;
        var safe = Sanitize(path, value);
        if (displayed.Count < MaxItems)
            displayed.Add(new ReportMetadataItem(path, safe));
        if (named.Count < MaxNamed && IsFactName(path))
            named.Add(new ReportMetadataItem(path, safe));
    }

    /// <summary>
    /// Значение попадает в отчёт: секретные поля маскируются, а из адреса удаляются
    /// учётные данные, параметры запроса и фрагмент.
    /// </summary>
    private static string Sanitize(string name, string value)
    {
        if (SensitiveNames.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return Hidden;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            return value;
        return uri.Scheme + "://" + uri.Host +
            (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + uri.AbsolutePath;
    }

    /// <summary>
    /// Имя поля, которое может попасть в сводку: известное имя, суффикс вложенного пути
    /// либо параметр запуска. Отбор не зависит от предела отображаемого списка.
    /// </summary>
    private static bool IsFactName(string name) =>
        FactNames.Any(names => names.Any(candidate =>
            name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase))) ||
        SamplingHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string value) =>
        value.Length <= MaxValueChars ? value : value[..MaxValueChars] + "…";
}