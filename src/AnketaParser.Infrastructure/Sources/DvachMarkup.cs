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
    internal string Text => string.Join('\n', Lines.Select(line => line.Text)).Trim();
}

internal sealed partial class DvachMarkup
{
    private readonly StringBuilder _line = new();
    private readonly List<BodyLine> _lines = [];
    private readonly List<BodyLink> _links = [];
    private readonly List<string> _quotes = [];
    private readonly List<ReplyTarget> _replies = [];

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

        if (element.LocalName is "script" or "style" or "template" or "svg" or "math" or "iframe" or "textarea" ||
            element.HasAttribute("hidden"))
            return;
        if (element.LocalName is "blockquote" or "q" || element.ClassList.Contains("unkfunc") || element.ClassList.Contains("quote"))
        {
            _quotes.Add(element.TextContent);
            // Пробел сохраняет границу слов вокруг удалённой inline-цитаты.
            _line.Append(' ');
            return;
        }
        if (element.LocalName == "a")
        {
            var href = element.GetAttribute("href");
            if (element.ClassList.Contains("post-reply-link"))
            {
                // Цитатная строка может содержать такую же ссылку, как адресный ответ.
                if (_line.ToString().TrimStart().StartsWith('>'))
                {
                    Append(element.TextContent);
                    return;
                }
                _replies.Add(ParseReply(element, href));
                _line.Append(' ');
                return;
            }
            if (!string.IsNullOrWhiteSpace(href))
                _links.Add(new BodyLink(_line.Length, href, element.TextContent));
        }
        if (element.LocalName == "br")
        {
            FlushLine();
            return;
        }
        var block = element.LocalName is "p" or "div" or "li" or "pre" or "hr" or "tr" or "section"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
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
            !IsReplyLabel(element.TextContent.Trim(), number, thread))
            throw new SourceFailure("invalid_reply_link", "parse");

        return new ReplyTarget(match.Groups[1].Value, thread, number);
    }

    private static bool IsReplyLabel(string label, long number, long thread)
    {
        var expected = ">>" + DvachSnapshot.PostId(number);
        return label == expected || (number == thread && label == expected + " (OP)");
    }

    private static bool TryNumber(string? value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

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
        if (text.TrimStart().StartsWith('>'))
            _quotes.Add(text);
        else
            _lines.Add(new BodyLine(text, _links.ToArray()));
        _line.Clear();
        _links.Clear();
    }

    [GeneratedRegex(@"\A(?:https://2ch\.org)?/([a-z0-9]{1,16})/res/([0-9]+)\.html#([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPath();
}