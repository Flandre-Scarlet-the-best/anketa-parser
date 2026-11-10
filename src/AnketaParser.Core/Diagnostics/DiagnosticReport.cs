namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Aggregate result of one diagnostics run: runtime, configuration sources and dependency probes.
/// </summary>
/// <param name="GeneratedAtUtc">Moment the report was produced.</param>
/// <param name="Status">Aggregate readiness status.</param>
/// <param name="Runtime">Secret-free runtime snapshot.</param>
/// <param name="ConfigurationSources">Configuration sources that contributed to the effective settings.</param>
/// <param name="Probes">Individual dependency results in registration order.</param>
public sealed record DiagnosticReport(
    DateTimeOffset GeneratedAtUtc,
    HealthStatus Status,
    RuntimeSnapshot Runtime,
    IReadOnlyList<ConfigurationSourceInfo> ConfigurationSources,
    IReadOnlyList<ProbeResult> Probes)
{
    /// <summary>True when at least one configured dependency failed its check.</summary>
    public bool HasFailedDependency => Probes.Any(static probe => probe.Status == ProbeStatus.Unhealthy);
}