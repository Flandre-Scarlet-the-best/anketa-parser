using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AnketaParser.Infrastructure.Sources;

internal sealed record ReplyTarget(string Board, long Thread, long Post);
internal sealed record BodyLink(int Offset, string Target, string Label);
internal sealed record BodyLine(string Text, IReadOnlyList<BodyLink> Links);
internal sealed record DvachBody(IReadOnlyList<BodyLine> Lines, IReadOnlyList<string> Quotes, IReadOnlyList<ReplyTarget> Replies)
{
    // Строки идут без пустых элементов: номер строки и смещение контакта в
    // сериализованном тексте совпадают с координатами разбора.
    internal string Text => string.Join('\n', Lines.Select(line => line.Text));
}

internal sealed partial class DvachMarkup
{
    private readonly StringBuilder _line = new();
    private readonly List<BodyLine> _lines = [];
    private readonly List<BodyLink> _links = [];
    private readonly List<string> _quotes = [];
    private readonly List<ReplyTarget> _replies = [];
    private string? _activeHref;
    private int _linkOffset;
    private bool _linkRecorded;
    private bool? _quoteLine;

    internal static DvachBody Parse(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(string.Empty);
        var body = document.Body!;
        var markup = new DvachMarkup();
        foreach (var node in parser.ParseFragment(html, body))
            markup.Visit(node, 0);
        markup.FlushLine();
        return new DvachBody(markup._lines, markup._quotes, markup._replies.Distinct().ToArray());
    }

    private void Visit(INode node, int depth)
    {
        if (depth > 128)
            throw new SourceFailure("markup_too_deep", "parse");

        if (node is IText text)
        {
            Append(text.Data);
            return;
        }
        if (node is not IElement element)
            return;

        if (IsHidden(element))
            return;
        if (element.LocalName is "blockquote" or "q" || element.ClassList.Contains("unkfunc") || element.ClassList.Contains("quote"))
        {
            _quotes.Add(element.TextContent);
            // Блочная цитата разделяет авторские строки, поэтому следующий текст не
            // приклеивается к строке с контактом. Строчная цитата сохраняет границу слов.
            if (element.LocalName == "blockquote")
                FlushLine();
            else
                _line.Append(' ');
            return;
        }
        if (element.LocalName == "a")
        {
            var href = element.GetAttribute("href");
            if (element.ClassList.Contains("post-reply-link"))
            {
                // Цитатная строка может содержать такую же ссылку, как адресный ответ.
                if (IsQuoteLine())
                {
                    // Подпись внутри цитаты остаётся в цитате целиком: перенос внутри
                    // подписи не открывает авторскую строку.
                    _line.Append(element.TextContent.ReplaceLineEndings(" "));
                    return;
                }
                _replies.Add(ParseReply(element, href));
                _line.Append(' ');
                return;
            }
            if (!string.IsNullOrWhiteSpace(href))
            {
                // Подпись — только тот текст, который обход добавил для этой ссылки:
                // TextContent включал бы скрытые и не выводимые узлы.
                _activeHref = href;
                _linkOffset = _line.Length;
                _linkRecorded = false;
                foreach (var child in element.ChildNodes)
                    Visit(child, depth + 1);
                RecordActiveLink();
                _activeHref = null;
                return;
            }
        }
        if (element.LocalName == "br")
        {
            FlushLine();
            return;
        }
        var block = IsBlock(element);
        if (block && _line.Length != 0)
            FlushLine();
        foreach (var child in element.ChildNodes)
            Visit(child, depth + 1);
        if (block)
            FlushLine();
    }

    private static ReplyTarget ParseReply(IElement element, string? href)
    {
        var match = ReplyPath().Match(href ?? string.Empty);
        if (!match.Success || !TryNumber(match.Groups[2].Value, out var thread) ||
            !TryNumber(match.Groups[3].Value, out var number) ||
            !TryNumber(element.GetAttribute("data-thread"), out var dataThread) || thread != dataThread ||
            !TryNumber(element.GetAttribute("data-num"), out var dataNum) || number != dataNum ||
            !IsReplyLabel(VisibleText(element).Trim(), number, thread))
            throw new SourceFailure("invalid_reply_link", "parse");

        return new ReplyTarget(match.Groups[1].Value, thread, number);
    }

    /// <summary>Видимый текст поддерева. Подпись ответа проверяется так же, как подпись обычной ссылки.</summary>
    private static string VisibleText(INode node)
    {
        if (node is IText text)
            return text.Data;
        if (node is not IElement element)
            return string.Empty;
        if (IsHidden(element))
            return string.Empty;
        // Граница строки сохраняется: подпись `>>12` и `3` через `<br>` не равна `>>123`.
        if (element.LocalName == "br")
            return "\n";
        if (element.LocalName is "blockquote" or "q" || element.ClassList.Contains("unkfunc") || element.ClassList.Contains("quote"))
            return string.Empty;
        // Блочный элемент разделяет строки так же, как при разборе авторского текста.
        var block = IsBlock(element);
        var builder = new StringBuilder();
        if (block)
            builder.Append('\n');
        foreach (var child in element.ChildNodes)
            builder.Append(VisibleText(child));
        if (block)
            builder.Append('\n');
        return builder.ToString();
    }

    private static bool IsReplyLabel(string label, long number, long thread)
    {
        var expected = ">>" + DvachSnapshot.PostId(number);
        return label == expected || (number == thread && label == expected + " (OP)");
    }

    private static bool IsHidden(IElement element) =>
        element.LocalName is "script" or "style" or "template" or "svg" or "math" or "iframe" or "textarea" ||
        element.HasAttribute("hidden");

    private static bool IsBlock(IElement element) =>
        element.LocalName is "p" or "div" or "li" or "pre" or "hr" or "tr" or "section"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static bool TryNumber(string? value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    /// <summary>Строка начинается с цитатной метки. Признак запоминается на строку.</summary>
    private bool IsQuoteLine()
    {
        if (_quoteLine is { } known)
            return known;
        var trimmed = _line.ToString().TrimStart();
        if (trimmed.Length == 0)
            return false;
        _quoteLine = trimmed[0] == '>';
        return _quoteLine.Value;
    }

    private void Append(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            _line.Append(text, start, i - start);
            FlushLine();
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        _line.Append(text, start, text.Length - start);
    }

    private void FlushLine()
    {
        var text = _line.ToString();
        var quoted = IsQuoteLine();
        if (!quoted)
            RecordActiveLink();
        if (quoted)
            _quotes.Add(text);
        else if (text.Length != 0)
            _lines.Add(new BodyLine(text, _links.ToArray()));
        _line.Clear();
        _links.Clear();
        _linkOffset = 0;
        _quoteLine = null;
    }

    private void RecordActiveLink()
    {
        if (_activeHref is null || _linkRecorded)
            return;
        var length = _line.Length - _linkOffset;
        var label = _line.ToString(_linkOffset, length);
        if (string.IsNullOrWhiteSpace(label))
            return;
        _links.Add(new BodyLink(_linkOffset, _activeHref, label));
        _linkRecorded = true;
    }

    [GeneratedRegex(@"\A(?:https://2ch\.org)?/([a-z0-9]{1,16})/res/([0-9]+)\.html#([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPath();
}