using System.Globalization;

using Microsoft.Extensions.Configuration;

namespace AnketaParser.Host;

/// <summary>
/// Defensive reads of configuration values that are needed before typed options exist.
/// Invalid text is left for the options validators to report with a clear message.
/// </summary>
internal static class ConfigurationValues
{
    public static int TryGetInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}