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
                offset = found + token.Length;
                // Короткие идентификаторы могут совпасть с частью обычного слова.
                if ((found == 0 || !IsWord(text[found - 1])) && (offset == text.Length || !IsWord(text[offset])))
                    Array.Fill(buffer, SemanticContract.Redacted, found, token.Length);
            }
        }
        foreach (Match match in Address().Matches(text))
            Array.Fill(buffer, SemanticContract.Redacted, match.Index, match.Length);
        return new string(buffer);
    }

    private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';

    [GeneratedRegex(@"(?:https?://|(?:telegram\.me|t\.me)/|(?:mailto|xmpp):)\S+|[\w.%+-]+@[\w.-]+|@[\w_]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 100)]
    private static partial Regex Address();
}
