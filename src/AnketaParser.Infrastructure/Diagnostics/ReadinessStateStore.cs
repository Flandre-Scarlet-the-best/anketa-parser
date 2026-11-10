using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Infrastructure.Diagnostics;

/// <summary>
/// Holds the most recent aggregated health report so that long-running components and future
/// readiness endpoints can read it without re-running probes.
/// </summary>
public sealed class ReadinessStateStore
{
    private DiagnosticReport? _lastReport;

    /// <summary>The most recent report, or <see langword="null"/> before the first run.</summary>
    public DiagnosticReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>Replaces the stored report.</summary>
    public void Update(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Volatile.Write(ref _lastReport, report);
    }
}