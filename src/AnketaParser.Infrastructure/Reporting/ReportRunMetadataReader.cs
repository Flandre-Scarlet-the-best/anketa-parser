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
    private const int MaxValueChars = 300;
    private const int MaxSamplingItems = 12;

    private static readonly string[] ModelNames = ["generalName", "modelName", "model.name", "name"];
    private static readonly string[] EngineNames = ["runtime", "backend", "engine"];
    private static readonly string[] EngineVersionNames = ["buildVersion", "engineVersion", "version"];
    private static readonly string[] QuantizationNames = ["quantization", "quantizationLabel"];
    private static readonly string[] HashNames = ["sha256", "modelSha256", "ggufSha256"];
    private static readonly string[] EndpointNames = ["baseUrl", "endpoint", "modelsUrl"];

    private static readonly string[] SamplingHints =
        ["temperature", "top_p", "top_k", "min_p", "repeat_penalty", "repeat_last_n", "seed", "n_ctx", "ctx", "max_output"];

    /// <summary>Читает файл метаданных запуска.</summary>
    /// <param name="path">Путь к JSON-файлу.</param>
    /// <param name="cancellationToken">Токен отмены чтения.</param>
    /// <returns>Метаданные запуска с полным списком плоских значений.</returns>
    /// <exception cref="ReportDiagnosticException">Файл недоступен или содержит некорректный JSON.</exception>
    public static async Task<ReportRunMetadataView> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
            List<ReportMetadataItem> all = [];
            Flatten(document.RootElement, string.Empty, all);
            return new ReportRunMetadataView(path, true, Summarize(all), all);
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

    private static void Flatten(JsonElement element, string path, List<ReportMetadataItem> items)
    {
        if (items.Count >= MaxItems)
            return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var component = property.Name.All(character => char.IsLetterOrDigit(character) || character == '_') &&
                        property.Name.Length > 0
                        ? (path.Length == 0 ? property.Name : "." + property.Name)
                        : "[" + JsonSerializer.Serialize(property.Name) + "]";
                    Flatten(property.Value, path + component, items);
                    if (items.Count >= MaxItems)
                        return;
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), items);
                    if (items.Count >= MaxItems)
                        return;
                    index++;
                }

                break;
            case JsonValueKind.String:
                items.Add(new ReportMetadataItem(path, Shorten(element.GetString() ?? string.Empty)));
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                items.Add(new ReportMetadataItem(path, Shorten(element.GetRawText())));
                break;
            default:
                // null не добавляется: отсутствие значения не является фактом запуска.
                break;
        }
    }

    private static string Shorten(string value) =>
        value.Length <= MaxValueChars ? value : value[..MaxValueChars] + "…";
}