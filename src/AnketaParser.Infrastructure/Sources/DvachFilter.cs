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
    internal const string OpeningPost = "opening_post";
    internal const string NoOwnContact = "no_own_contact";
    internal const string OtherReply = "other_reply";
    internal const string ProcessorVersion = "profile-analysis-v1";
    internal const int MaxAttempts = 5;

    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Address = @"[A-Za-z0-9_%+-]+(?:\.[A-Za-z0-9_%+-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*";
    private const string TelegramId = @"[A-Za-z][A-Za-z0-9_]{4,31}";
    private const string DiscordId = @"(?:[A-Za-z0-9_.-]{2,32}#[0-9]{4}|[A-Za-z0-9_.]{2,32})";
    private const string Platforms = @"telegram|телеграмм|телеграм|телега|телегу|tg|тг|discord|дискорд|дс|xmpp|jabber|жаббер|жабка|email|e-mail|почта|мыло";
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
                ? "op_reply" : OtherReply;
        var contacts = reply == "op" ? [] : Extract(body.Lines);
        var ambiguous = contacts.GroupBy(c => c.Platform, StringComparer.Ordinal)
            .Any(group => group.Select(c => c.NormalizedValue).Distinct(StringComparer.Ordinal).Skip(1).Any());
        var reason = reply == "op" ? OpeningPost : contacts.Count == 0 ? NoOwnContact : Candidate;
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

            var text = line.Text;
            var number = index + 1;
            List<(int Start, int End)> occupied = [];
            List<(int Start, int End)> labeled = [];
            List<(int Offset, DetectedContact Contact)> found = [];

            void Record(int offset, int length, string platform, string raw, string value, decimal confidence, string origin)
            {
                if (occupied.Any(span => offset < span.End && offset + length > span.Start))
                    return;
                var contact = Create(platform, raw, value, confidence, number, offset, origin);
                if (contact is null)
                    return;
                occupied.Add((offset, offset + length));
                found.Add((offset, contact));
            }

            // Явный адрес имеет приоритет над соседней меткой другой платформы.
            foreach (Match match in ExplicitContact().Matches(text))
            {
                if (InsideUrl(text, match.Index))
                    continue;
                var label = PreviousPlatform(text, match.Index, lines, index);
                if (match.Groups["telegram"].Success)
                    Record(match.Index, match.Length, "telegram", match.Value, match.Groups["telegram"].Value, 0.9m, "text");
                else if (match.Groups["xmpp"].Success)
                    Record(match.Index, match.Length, "xmpp", match.Value, match.Groups["xmpp"].Value, 0.9m, "text");
                else
                {
                    var value = match.Groups["mailto"].Success ? match.Groups["mailto"].Value : match.Groups["email"].Value;
                    var platform = label == "xmpp" && !match.Groups["mailto"].Success ? "xmpp" : "email";
                    Record(match.Index, match.Length, platform, match.Value, value, 0.85m, "text");
                }
            }

            // Явные контакты строки: они уже описывают значение справа от метки.
            List<(int Offset, string Platform)> explicitContacts =
                found.Select(item => (item.Offset, item.Contact.Platform)).ToList();

            foreach (Match match in LabeledValue().Matches(text))
            {
                var platform = Platform(match.Groups["platform"].Value);
                var value = match.Groups["value"];
                if (InsideUrl(text, value.Index))
                    continue;
                if (platform is not ("telegram" or "discord") || !IsLabeledValue(platform, value.Value))
                    continue;
                // Слэш допустим только как разделитель списка значений: остаток адреса
                // (`tg: alice_name/path`) не должен давать первый контакт.
                var chain = SlashList(platform, text, value.Index + value.Length);
                if (chain is null)
                    continue;
                // Прямое правило связало метку со значением справа: обратное правило
                // для этой метки больше не участвует в распознавании.
                labeled.Add((match.Index, value.Index + value.Length));
                Record(value.Index, value.Length, platform, value.Value, value.Value.TrimStart('@'), 0.8m, "label");
                foreach (var item in chain)
                    Record(item.Index, item.Value.Length, platform, item.Value, item.Value.TrimStart('@'), 0.8m, "label");
                var next = value.Index + value.Length;
                while (LabeledListItem().Match(text, next) is { Success: true } item)
                {
                    value = item.Groups["value"];
                    if (PlatformOnly().IsMatch(value.Value) || !IsLabeledValue(platform, value.Value))
                        break;
                    Record(value.Index, value.Length, platform, value.Value, value.Value.TrimStart('@'), 0.8m, "label");
                    next = item.Index + item.Length;
                }
            }
            // Метка на предыдущей строке относится только к первому токену следующей строки.
            if (index > 0 && PlatformOnly().Match(lines[index - 1].Text) is { Success: true } previous)
            {
                var value = FirstValue().Match(text).Groups["value"];
                var platform = Platform(previous.Groups["platform"].Value);
                if (value.Success && platform == "telegram" && TelegramValue().IsMatch(value.Value))
                    Record(value.Index, value.Length, platform, value.Value, value.Value.TrimStart('@'), 0.8m, "label");
                else if (value.Success && platform == "discord" && DiscordValue().IsMatch(value.Value.TrimStart('@')))
                    Record(value.Index, value.Length, platform, value.Value, value.Value.TrimStart('@'), 0.8m, "label");
            }
            foreach (Match match in ReverseTelegram().Matches(text))
            {
                var value = match.Groups["value"];
                // Слово перед меткой не является вторым контактом, если эта метка уже
                // связана прямым правилом со значением справа от неё или справа от неё
                // распознан явный контакт.
                if (labeled.Any(span => match.Groups["label"].Index >= span.Start && match.Groups["label"].Index < span.End) ||
                    explicitContacts.Any(item => item.Offset > match.Groups["label"].Index && item.Platform == "telegram"))
                    continue;
                if (!InsideUrl(text, value.Index))
                    Record(value.Index, value.Length, "telegram", value.Value, value.Value.TrimStart('@'), 0.8m, "label");
            }
            foreach (Match match in BareTelegram().Matches(text))
            {
                if (!InsideUrl(text, match.Index))
                    Record(match.Index, match.Length, "telegram", match.Value, match.Value[1..], 0.6m, "text");
            }
            foreach (var link in line.Links)
            {
                if (link.Target.Length > 2048 || string.IsNullOrWhiteSpace(link.Label))
                    continue;
                var match = ExplicitContact().Match(link.Target);
                if (!match.Success || match.Length != link.Target.Length)
                    continue;
                // Схема в шаблоне необязательна, но адрес обязан вести на Telegram:
                // `t.me/alice_name` без схемы разрешается относительно страницы треда.
                if (match.Groups["telegram"].Success && !IsTelegramTarget(link.Target))
                    continue;
                // Почтовый адрес из href принимается только как почтовая ссылка:
                // `alice@example.invalid` в href — относительный путь, а не адрес.
                if (!match.Groups["telegram"].Success && !match.Groups["xmpp"].Success && !match.Groups["mailto"].Success)
                    continue;
                var platform = match.Groups["telegram"].Success ? "telegram" : match.Groups["xmpp"].Success ? "xmpp" : "email";
                var value = match.Groups[platform == "telegram" ? "telegram" : platform == "xmpp" ? "xmpp" :
                    match.Groups["mailto"].Success ? "mailto" : "email"].Value;
                var contact = Create(platform, match.Value, value, 0.9m, number, link.Offset, "href");
                if (contact is not null && !found.Any(item => item.Offset >= link.Offset && item.Offset < link.Offset + link.Label.Length &&
                    item.Contact.Platform == contact.Platform && item.Contact.NormalizedValue == contact.NormalizedValue))
                    found.Add((link.Offset, contact));
            }
            result.AddRange(found.OrderBy(item => item.Offset).Select(item => item.Contact));
        }
        return result.Distinct().ToList();
    }

    private static bool IsLabeledValue(string platform, string value) => platform switch
    {
        "telegram" => TelegramValue().IsMatch(value),
        "discord" => DiscordValue().IsMatch(value.TrimStart('@')),
        _ => false,
    };

    /// <summary>Значения после слэшей; null, если цепочка содержит недопустимый остаток.</summary>
    private static List<(int Index, string Value)>? SlashList(string platform, string text, int offset)
    {
        List<(int Index, string Value)> values = [];
        var position = offset;
        while (position < text.Length && text[position] == '/')
        {
            var item = SlashValue().Match(text, position);
            if (!item.Success || !IsLabeledValue(platform, item.Groups["value"].Value))
                return null;
            values.Add((item.Groups["value"].Index, item.Groups["value"].Value));
            position = item.Index + item.Length;
        }
        return values;
    }

    private static bool IsTelegramTarget(string target)
    {
        // Адрес ссылки обязан быть абсолютным http(s)-адресом Telegram: относительный
        // `t.me/alice_name` разрешается браузером относительно страницы треда.
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        return uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase);
    }

    private static bool InsideUrl(string text, int offset)
    {
        var start = offset;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('"' or '\'' or '<' or '>'))
            start--;
        var prefix = text.AsSpan(start, offset - start);
        if (prefix.Contains("://", StringComparison.Ordinal))
            return true;
        // Слэш между адресами допустим, но адрес внутри произвольного пути — нет.
        if (offset > 0 && text[offset - 1] == '/')
        {
            var beforeSlash = text[start..(offset - 1)];
            return !ExplicitContact().Matches(beforeSlash).Any(match => match.Index + match.Length == beforeSlash.Length);
        }
        return false;
    }

    private static string? PreviousPlatform(string text, int offset, IReadOnlyList<BodyLine> lines, int index)
    {
        var prefix = text[..offset];
        var match = PlatformAtEnd().Match(prefix);
        if (!match.Success && string.IsNullOrWhiteSpace(prefix) && index > 0)
            match = PlatformOnly().Match(lines[index - 1].Text);
        return match.Success ? Platform(match.Groups["platform"].Value) : null;
    }

    private static string Platform(string value) => value.ToLowerInvariant() switch
    {
        "tg" or "тг" or "telegram" or "телеграм" or "телеграмм" or "телега" or "телегу" => "telegram",
        "discord" or "дискорд" or "дс" => "discord",
        "xmpp" or "jabber" or "жаббер" or "жабка" => "xmpp",
        _ => "email",
    };

    private static DetectedContact? Create(string platform, string raw, string value, decimal confidence, int line, int offset, string origin)
    {
        var at = value.LastIndexOf('@');
        if (platform == "telegram" && TelegramReserved.Contains(value))
            return null;
        if (platform == "discord" && (value.Contains("..", StringComparison.Ordinal) || !value.Contains('#') && value.Length < 2))
            return null;
        if (platform == "email" && (at <= 0 || !value.AsSpan(at + 1).Contains('.')))
            return null;
        if (value.Length > 254)
            return null;

        var normalized = platform switch
        {
            "telegram" or "xmpp" => value.ToLowerInvariant(),
            "email" when at > 0 => value[..(at + 1)] + value[(at + 1)..].ToLowerInvariant(),
            _ => value,
        };
        var provenance = DvachProvenance.Create(line, offset, origin);
        return new DetectedContact(platform, raw, normalized, confidence, provenance);
    }

    [GeneratedRegex(@"(?<![\w@.:%+-])(?:(?:https?://)?(?:t\.me|telegram\.me)/(?<telegram>" + TelegramId +
        @")(?:/)?(?![\w/?#%+-])|xmpp:(?<xmpp>" + Address + @")|mailto:(?<mailto>" + Address + @")|(?<email>" + Address +
        @"))(?![\w@%+-]|\.[\w.])", IgnoreCase)]
    private static partial Regex ExplicitContact();

    [GeneratedRegex(@"(?<![\w@./:%+-])@" + TelegramId + @"(?![\w@/%+-]|\.[\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex BareTelegram();

    [GeneratedRegex(@"(?<![\w])(?<platform>" + Platforms + @")(?![\w])(?:\s*[:=—–-]\s*|\s+)[\""«]?(?<value>@?[A-Za-z0-9_.#-]+)[\""»]?(?![\w@.:%+#-])", IgnoreCase)]
    private static partial Regex LabeledValue();

    [GeneratedRegex(@"\G\s*/\s*[\""«]?(?<value>@?[A-Za-z0-9_.#-]+)(?=[ \t]*(?:/|\z))", RegexOptions.CultureInvariant)]
    private static partial Regex SlashValue();

    [GeneratedRegex(@"\G[\""»]?\s*[,;|/]\s*[\""«]?(?<value>@?[A-Za-z0-9_.#-]+)(?![\w@./:%+#-])", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledListItem();

    [GeneratedRegex(@"\A@?" + TelegramId + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex TelegramValue();

    [GeneratedRegex(@"\A" + DiscordId + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex DiscordValue();

    [GeneratedRegex(@"(?<![\w@./:%+-])(?<value>@?" + TelegramId + @")\s+(?<label>telegram|телеграмм|телеграм|телега|tg|тг)(?!\w)", IgnoreCase)]
    private static partial Regex ReverseTelegram();

    [GeneratedRegex(@"(?<!\w)(?<platform>" + Platforms + @")(?:\s*[:=—–-]\s*|\s*)\z", IgnoreCase)]
    private static partial Regex PlatformAtEnd();

    [GeneratedRegex(@"\A\s*(?<platform>" + Platforms + @")(?:\s*[:=—–-]\s*|\s*)\z", IgnoreCase)]
    private static partial Regex PlatformOnly();

    [GeneratedRegex(@"\A\s*[\""«]?(?<value>@?[A-Za-z0-9_.#-]+)(?![\w@./:%+#-])", RegexOptions.CultureInvariant)]
    private static partial Regex FirstValue();
}