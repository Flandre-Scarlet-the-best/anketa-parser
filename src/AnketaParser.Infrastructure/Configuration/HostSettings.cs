namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Settings for the long-running host process itself. Bound from the <c>Host</c> configuration section.
/// </summary>
public sealed class HostSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Host";

    /// <summary>Logical instance name used to tell hosts apart in logs. Safe default.</summary>
    public string InstanceName { get; set; } = "anketa-parser";

    /// <summary>Seconds the host may spend shutting down before it is terminated.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 20;

    /// <summary>Whether dependency diagnostics run once during startup. Defaults to enabled.</summary>
    public bool StartupDiagnosticsEnabled { get; set; } = true;

    /// <summary>Interval in seconds between periodic readiness checks. Zero disables monitoring.</summary>
    public int ReadinessCheckIntervalSeconds { get; set; } = 60;
}