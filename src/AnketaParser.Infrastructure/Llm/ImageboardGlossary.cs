using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnketaParser.Infrastructure.Llm;

internal static partial class ImageboardGlossary
{
    private sealed record Entry(string[] Forms, string Meaning, string? Field = null, string? Label = null);

    private static readonly Entry[] Entries =
    [
        new(["тян", "тяночка"], "female person; author gender only in self-description", "gender", "Девушка"),
        new(["кун", "кунчик"], "male person; author gender only in self-description", "gender", "Парень"),
        new(["ж"], "female in a self-introduction, including compact forms like ж24", "gender", "Девушка"),
        new(["м"], "male in a self-introduction, including compact forms like м30", "gender", "Парень"),
        new(["дс1", "мск"], "Moscow only in geographic context; ДС allows spaces or a dash before the digit", "location", "Москва"),
        new(["дс2", "спб", "питер"], "Saint Petersburg only in geographic context; ДС allows spaces or a dash before the digit", "location", "Санкт-Петербург"),
        new(["екб"], "Yekaterinburg only in geographic context", "location", "Екатеринбург"),
        new(["лвл", "lvl", "левел"], "level; age only when clearly used as the author's age, not in a game"),
        new(["тг", "телега"], "Telegram in contact context, not a fact about the author", "contact"),
        new(["дис", "дс"], "Discord in contact context, distinct from numbered city abbreviations", "contact"),
        new(["ирл", "in real life"], "in-person meeting, a possible goal, not a place", "non_location"),
        new(["инет", "интернет", "интернеты", "онлайн", "своего города"], "online or unspecified place, never a geographic location", "non_location"),
        new(["сап", "го в лс", "лс", "го поболтаем"], "greeting or invitation to chat, depending on intent"),
        new(["би", "бисексуал", "бисексуальный", "бисексуальная", "бисексуальное"], "bisexuality, not author gender", "orientation"),
    ];

    internal static string Instructions { get; } = "Context glossary (do not translate or replace source values):\n" +
        string.Join('\n', Entries.Select(entry => $"- {string.Join(", ", entry.Forms)}: {entry.Meaning}."));

    internal static bool IsShortGender(string value) => value.Length == 1 && Find(value)?.Field == "gender";

    internal static bool RequiresContext(string field, string value) =>
        Find(value)?.Field == field && (field == "location" || IsShortGender(value));

    internal static bool IsNumberedCityInContext(string value, ReadOnlySpan<char> prefix) =>
        NumberedDiscord().IsMatch(value) && Normalize("location", value, prefix, []) is not null;

    internal static bool Allows(string field, string value)
    {
        var entry = Find(value);
        return field switch
        {
            "gender" => entry?.Field is not ("orientation" or "location" or "contact"),
            "interests" => entry?.Field != "location",
            "location" => entry?.Field is not ("non_location" or "contact" or "gender" or "orientation") &&
                (!NumberedDiscord().IsMatch(value) || entry?.Field == "location"),
            _ => true,
        };
    }

    internal static string? Normalize(string field, string value, ReadOnlySpan<char> prefix, ReadOnlySpan<char> suffix)
    {
        if (Find(value) is not { } entry || entry.Field != field)
            return null;
        // Словарная форма недостаточна: нужна роль автора, а для города ещё и географический контекст.
        return field switch
        {
            "gender" when SelfPrefix().IsMatch(prefix) || IsHeaderPrefix(prefix) && HeaderAge().IsMatch(suffix) => entry.Label,
            "location" when GeographicPrefix().IsMatch(prefix) => entry.Label,
            _ => null,
        };
    }

    private static bool IsHeaderPrefix(ReadOnlySpan<char> prefix)
    {
        foreach (var rune in prefix.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && rune.Value is not (0xFE0E or 0xFE0F) && Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.Format))
                return false;
        }
        return true;
    }

    internal static string FingerprintLabels { get; } = string.Join('\n', Entries.Select(entry =>
        string.Create(CultureInfo.InvariantCulture, $"{string.Join(',', entry.Forms)}:{entry.Field}:{entry.Label}")));

    private static Entry? Find(string value)
    {
        var key = NumberedDiscord().IsMatch(value) ? "дс" + value[^1] : value;
        return Entries.FirstOrDefault(entry => entry.Forms.Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\Aдс\s*[-–—]?\s*[0-9]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedDiscord();

    [GeneratedRegex(@"(?:\A|,)\s*(?:я\s*[-–—:]?\s*|пол\s*[:=]\s*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfPrefix();

    [GeneratedRegex(@"\A\s*,?\s*[0-9]{1,3}(?![\w+–—-])", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderAge();

    [GeneratedRegex(@"(?:\b(?:живу|нахожусь)\s+в|(?:\A|[,;]\s*|\b(?:я|сам|сама)\s+)\s*(?:из|город))[\s:]*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeographicPrefix();
}