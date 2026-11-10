namespace AnketaParser.Core.Diagnostics;

/// <summary>Outcome of a single readiness probe.</summary>
public enum ProbeStatus
{
    /// <summary>The dependency answered and reported itself healthy.</summary>
    Healthy,

    /// <summary>The dependency is configured but did not answer or reported a failure.</summary>
    Unhealthy,

    /// <summary>The dependency is intentionally not configured, so readiness is not claimed.</summary>
    Skipped,
}