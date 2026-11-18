using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using AnketaParser.Core.Persistence;

namespace AnketaParser.Infrastructure.Sources;

internal sealed record DetectedContact(string Platform, string RawValue, string NormalizedValue, decimal Confidence, string Provenance);
internal sealed record PreparedPost(PostInput Post, string Reason, string Reply, IReadOnlyList<DetectedContact> Contacts, bool Ambiguous);

internal static partial class DvachFilter
{
    internal const string Candidate = "candidate";
    internal const string Excluded = "moderation_excluded";
    internal const string ProcessorVersion = "profile-analysis-v1";
    internal const int MaxAttempts = 5;

    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Address = @"[A-Za-z0-9_%+-]+(?:\.[A-Za-z0-9_%+-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*";
    private const string TelegramId = @"[A-Za-z][A-Za-z0-9_]{4,31}";
    private const string DiscordId = @"(?:[a-z0-9_.]{2,32}|[A-Za-z0-9_.-]{2,32}#[0-9]{4})";
    private const string TelegramLink = @"(?:https?://)?(?:t\.me|telegram\.me)/" + TelegramId + @"/?";
    private const string MailLike = @"(?:xmpp:|mailto:)?" + Address;
    private static readonly HashSet<string> TelegramReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "addemoji", "addlist", "addstickers", "boost", "confirmphone", "contact", "giftcode", "invoice",
        "iv", "joinchat", "login", "m", "nft", "proxy", "resolve", "setlanguage", "share", "socks", "story",
    };

    internal static PreparedPost Prepare(DvachThread thread, DvachPost post, string sourceKey)
    {
        var body = DvachMarkup.Parse(post.Html);
        var reply = post.Number == thread.Number ? "op"
            : body.Replies.Count == 0 ? "none"
            : body.Replies.All(r => r.Board == thread.Board && r.Thread == thread.Number && r.Post == thread.Number)
                ? "op_reply" : "other_reply";
        var contacts = reply is "op" or "other_reply" ? [] : Extract(body.Lines);
        var ambiguous = contacts.GroupBy(c => c.Platform, StringComparer.Ordinal)
            .Any(group => group.Select(c => c.NormalizedValue).Distinct(StringComparer.Ordinal).Skip(1).Any());
        var reason = reply switch
        {
            "op" => "opening_post",
            "other_reply" => "reply_to_other_post",
            _ when contacts.Count == 0 => "no_own_contact",
            _ => Candidate,
        };
        var id = DvachSnapshot.PostId(post.Number);
        var metadata = JsonSerializer.Serialize(new
        {
            format = "dvach_thread_json",
            threadParent = post.Parent,
            text = body.Text,
            quotes = body.Quotes,
            replies = body.Replies,
        });

        return new PreparedPost(new PostInput(sourceKey, id, thread.Url + "#" + id, post.Html, post.PublishedAt, metadata),
            reason, reply, contacts, ambiguous);
    }

    private static List<DetectedContact> Extract(IReadOnlyList<BodyLine> lines)
    {
        List<DetectedContact> result = [];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            // Ограничение применяется к строке распознавания, исходный текст остаётся полным.
            if (line.Text.Length > 4096)
                continue;

            var context = OwnLead().Match(line.Text);
            var label = PlatformLead().Match(line.Text, context.Length);
            var platform = label.Success ? Platform(label.Groups["platform"].Value) : null;
            var start = label.Success ? label.Index + label.Length : context.Length;
            var content = line.Text[start..];
            start += content.Length - content.TrimStart().Length;
            content = content.Trim();
            var number = index + 1;

            if (content.Length == 0 && line.Links.Count == 0)
                continue;

            // Список разбирается целиком: границы одиночного адреса запрещают
            // разделители, поэтому ExplicitContact не находит элементы списка.
            var list = content.Length != 0 && ContactList().IsMatch(content);
            foreach (Match match in (list ? ListElement() : ExplicitContact()).Matches(content))
            {
                if (list || Standalone(content, match))
                    AddExplicit(result, match, platform, number, start + match.Index, "text");
            }
            if (platform is "telegram" or "discord" || platform == "xmpp" && !ExplicitContact().IsMatch(content))
            {
                var matches = LabelValues(platform).Matches(content);
                if (matches.Count > 0 && matches[^1].Index + matches[^1].Length == content.Length)
                {
                    foreach (Match match in matches)
                    {
                        var value = match.Groups["value"];
                        AddLabeled(result, value.Value, platform, number, start + value.Index);
                    }
                }
            }

            foreach (var link in line.Links)
            {
                var caption = link.Label.Trim();
                // Подписью ссылки может быть вся видимая строка, даже если OwnLead её поглотил.
                var wholeLine = content.Length == 0 && line.Text.Trim().Equals(caption, StringComparison.Ordinal);
                if (link.Target.Length > 2048 || !(wholeLine || content.Equals(caption, StringComparison.Ordinal)))
                    continue;
                var match = ExplicitContact().Match(link.Target);
                if (match.Success && match.Length == link.Target.Length && !match.Groups["email"].Success &&
                    !link.Target.Equals(caption, StringComparison.Ordinal))
                    AddExplicit(result, match, platform, number, link.Offset, "href");
            }
        }
        return result.Distinct().ToList();
    }

    private static bool Standalone(string content, Match match) =>
        content.AsSpan(0, match.Index).Trim().IsEmpty &&
        content.AsSpan(match.Index + match.Length).Trim().TrimEnd(".!)").IsEmpty;

    private static string Platform(string value) => value.ToLowerInvariant() switch
    {
        "tg" or "тг" or "telegram" or "телеграм" or "телега" or "телегу" => "telegram",
        "discord" or "дискорд" or "дс" => "discord",
        "xmpp" or "jabber" or "жаббер" or "жабка" => "xmpp",
        _ => "email",
    };

    private static Regex LabelValues(string platform) => platform switch
    {
        "telegram" => LabeledTelegram(),
        "discord" => LabeledDiscord(),
        _ => LabeledAddress(),
    };

    private static void AddExplicit(List<DetectedContact> result, Match match, string? label, int line, int offset, string origin)
    {
        if (match.Groups["telegram"].Success)
            Add(result, "telegram", match.Value, match.Groups["telegram"].Value, 0.9m, line, offset, origin);
        else if (match.Groups["xmpp"].Success)
            Add(result, "xmpp", match.Value, match.Groups["xmpp"].Value, 0.9m, line, offset, origin);
        else
        {
            if (match.Groups["email"].Success && label is "telegram" or "discord")
                return;
            var platform = label == "xmpp" && match.Groups["email"].Success ? "xmpp" : "email";
            var value = match.Groups["mailto"].Success ? match.Groups["mailto"].Value : match.Groups["email"].Value;
            Add(result, platform, match.Value, value, 0.85m, line, offset, origin);
        }
    }

    private static void AddLabeled(List<DetectedContact> result, string value, string platform, int line, int offset) =>
        Add(result, platform, value, value.TrimStart('@'), 0.8m, line, offset, "label");

    private static void Add(List<DetectedContact> result, string platform, string raw, string value, decimal confidence,
        int line, int offset, string origin)
    {
        var at = value.LastIndexOf('@');
        if (platform == "telegram" && TelegramReserved.Contains(value))
            return;
        if (platform == "discord" && (value.Contains("..", StringComparison.Ordinal) || !value.Contains('#') && value.Length < 2))
            return;
        if (platform == "email" && (at <= 0 || !value.AsSpan(at + 1).Contains('.')))
            return;
        if (value.Length > 254)
            return;

        // Локальная часть email и исторические Discord-теги сохраняют регистр.
        var normalized = platform switch
        {
            "telegram" or "xmpp" => value.ToLowerInvariant(),
            "email" when at > 0 => value[..(at + 1)] + value[(at + 1)..].ToLowerInvariant(),
            _ => value,
        };
        var provenance = string.Create(CultureInfo.InvariantCulture, $"dvach:body:{line}:{offset}:{origin}");
        result.Add(new DetectedContact(platform, raw, normalized, confidence, provenance));
    }

    [GeneratedRegex(@"\A\s*(?:(?:мои\s+)?контакты|для\s+связи|связь|мой\s+ник|пиши(?:те)?\s+мне|напиши(?:те)?\s+мне|мне\s+сюда)(?=\s|[:—–-]|\z)\s*[:—–-]?\s*", IgnoreCase)]
    private static partial Regex OwnLead();

    [GeneratedRegex(@"\G\s*(?:[0-9]{1,2}\.\s*)?(?:(?:мой|моя|в|пиши(?:те)?\s+в|напиши(?:те)?\s+в)\s+)?(?<platform>telegram|телеграм|телега|телегу|tg|тг|discord|дискорд|дс|xmpp|jabber|жаббер|жабка|email|e-mail|почта|мыло)\s*(?:[:=—–-]\s*|\s+)(?:пиши(?:те)?\s+)?(?=\S)", IgnoreCase)]
    private static partial Regex PlatformLead();

    [GeneratedRegex(@"(?<![\w@./:%+-])(?:(?:https?://)?(?:t\.me|telegram\.me)/(?<telegram>" + TelegramId +
        @")(?:/)?(?![\w/?#%+-])|xmpp:(?<xmpp>" + Address + @")|mailto:(?<mailto>" + Address + @")|(?<email>" + Address +
        @"))(?![\w@/%+-]|\.[\w.])", IgnoreCase)]
    private static partial Regex ExplicitContact();

    // Пробелы между элементами списка сопоставляются одним \s*: иначе два соседних
    // \s* дают экспоненциальный перебор вариантов на длинной строке.
    [GeneratedRegex(@"\A\s*(?:(?:" + TelegramLink + "|" + MailLike + @")\s*[,;|/]\s*)+(?:" + TelegramLink + "|" +
        MailLike + @")\s*\z", IgnoreCase)]
    private static partial Regex ContactList();

    // Элементы списка после проверки ContactList: границы строки уже доказаны.
    [GeneratedRegex(@"(?:https?://)?(?:t\.me|telegram\.me)/(?<telegram>" + TelegramId + @")/?|xmpp:(?<xmpp>" +
        Address + @")|mailto:(?<mailto>" + Address + @")|(?<email>" + Address + @")", IgnoreCase)]
    private static partial Regex ListElement();

    [GeneratedRegex(@"\G\s*(?:[,;|/]\s*)?[""«]?(?<value>@?" + TelegramId + @")[""»]?(?=(?:\s*[,;|/]|\s+@|\z))", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledTelegram();

    [GeneratedRegex(@"\G\s*(?:[,;|/]\s*)?[""«]?(?<value>@?" + DiscordId + @")[""»]?(?=(?:\s*[,;|/]|\s+@|\z))", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledDiscord();

    [GeneratedRegex(@"\G\s*(?:[,;|/]\s*)?[""«]?(?<value>" + Address + @")[""»]?(?=\s*(?:[,;|/]|\z))", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledAddress();
}