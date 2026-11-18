using System.Globalization;

namespace AnketaParser.Infrastructure.Sources;

internal static class DvachProvenance
{
    internal const string Prefix = "dvach:body:";

    internal static string Create(int line, int offset, string origin) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{line}:{offset}:{origin}");

    internal static string Coordinate(string provenance)
    {
        var separator = provenance.LastIndexOf(':');
        return separator < Prefix.Length ? provenance : provenance[..separator];
    }
}