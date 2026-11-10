namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Result of checking one external dependency. <see cref="Detail"/> must never contain secrets.
/// </summary>
/// <param name="Name">Stable probe name, for example <c>postgres</c>.</param>
/// <param name="Status">Whether the dependency is healthy, unhealthy or skipped.</param>
/// <param name="Detail">Human readable, secret-free explanation of the result.</param>
/// <param name="Duration">Time spent by the probe.</param>
public sealed record ProbeResult(string Name, ProbeStatus Status, string Detail, TimeSpan Duration)
{
    /// <summary>Creates a healthy result.</summary>
    public static ProbeResult Healthy(string name, string detail, TimeSpan duration) =>
        new(name, ProbeStatus.Healthy, detail, duration);

    /// <summary>Creates an unhealthy result.</summary>
    public static ProbeResult Unhealthy(string name, string detail, TimeSpan duration) =>
        new(name, ProbeStatus.Unhealthy, detail, duration);

    /// <summary>Creates a skipped result with zero duration.</summary>
    public static ProbeResult Skipped(string name, string detail) =>
        new(name, ProbeStatus.Skipped, detail, TimeSpan.Zero);
}