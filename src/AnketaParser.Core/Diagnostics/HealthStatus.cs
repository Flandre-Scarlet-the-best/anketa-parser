namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Aggregate readiness of the process and its dependencies.
/// The distinction matters: a live process is not the same as a ready one.
/// </summary>
public enum HealthStatus
{
    /// <summary>Every checked dependency answered successfully.</summary>
    Healthy,

    /// <summary>The process is fine, but an optional dependency is not configured or was skipped.</summary>
    Degraded,

    /// <summary>At least one configured dependency failed its check.</summary>
    Unhealthy,
}