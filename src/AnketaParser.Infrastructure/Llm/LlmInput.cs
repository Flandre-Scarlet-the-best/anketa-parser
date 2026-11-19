using System.Text.RegularExpressions;

namespace AnketaParser.Infrastructure.Llm;

internal static partial class LlmInput
{
    internal static string Redact(string text, IEnumerable<string> knownContacts)
    {
        var buffer = text.ToCharArray();
        foreach (var token in knownContacts.Where(token => token.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var offset = 0; offset < text.Length;)
            {
                var found = text.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;
                // Конфиденциальность важнее сохранения обычного слова, совпавшего с контактом.
                // Проверяем и перекрывающиеся вхождения, сохраняя координаты текста.
                Array.Fill(buffer, SemanticContract.Redacted, found, token.Length);
                offset = found + 1;
            }
        }
        foreach (Match match in Address().Matches(text))
            Array.Fill(buffer, SemanticContract.Redacted, match.Index, match.Length);
        return new string(buffer);
    }

    [GeneratedRegex(@"(?:https?://|(?:telegram\.me|t\.me)/|(?:mailto|xmpp):)\S+|[\w.%+-]+@[\w.-]+|@[\w_]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 100)]
    private static partial Regex Address();
}