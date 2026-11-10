namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Diagnostics behavior. Bound from the <c>Diagnostics</c> configuration section.
/// </summary>
public sealed class DiagnosticsSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Diagnostics";

    /// <summary>Maximum time in seconds a single dependency probe may take.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;
}