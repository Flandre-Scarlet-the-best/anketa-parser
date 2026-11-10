namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Runs every registered readiness probe and aggregates the results into a <see cref="DiagnosticReport"/>.
/// The runner only knows the <see cref="IReadinessProbe"/> contract, so it is shared by the long-running
/// host and by the agent CLI without either of them duplicating dependency logic.
/// </summary>
public sealed class DiagnosticsRunner
{
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly IRuntimeSnapshotProvider _runtime;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a runner over the given probes.</summary>
    public DiagnosticsRunner(
        IEnumerable<IReadinessProbe> probes,
        IRuntimeSnapshotProvider runtime,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = [.. probes];
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Registered probes in registration order.</summary>
    public IReadOnlyList<IReadinessProbe> Probes => _probes;

    /// <summary>
    /// Runs all probes sequentially with a per-probe timeout. Expected dependency failures are converted
    /// into <see cref="ProbeStatus.Unhealthy"/> results; only cancellation of the caller escapes.
    /// </summary>
    public async Task<DiagnosticReport> RunAsync(
        IReadOnlyList<ConfigurationSourceInfo> configurationSources,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configurationSources);

        var results = new List<ProbeResult>(_probes.Count);
        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunProbeAsync(probe, probeTimeout, cancellationToken).ConfigureAwait(false));
        }

        return new DiagnosticReport(
            _timeProvider.GetUtcNow(),
            Aggregate(results),
            _runtime.Capture(),
            configurationSources,
            results);
    }

    private static async Task<ProbeResult> RunProbeAsync(
        IReadinessProbe probe,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        var startedAt = TimeProvider.System.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(probeTimeout, TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await probe.CheckAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unhealthy(
                probe.Name,
                $"Probe did not finish within {probeTimeout.TotalSeconds:0.###}s.",
                TimeProvider.System.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException)
        {
            // The caller stopped the run; the probe result is not meaningful.
            throw;
        }
        catch (Exception exception)
        {
            // Never surface raw exception text: it may contain a connection string or credentials.
            return ProbeResult.Unhealthy(
                probe.Name,
                $"Probe failed with {exception.GetType().Name}.",
                TimeProvider.System.GetElapsedTime(startedAt));
        }
    }

    private static HealthStatus Aggregate(List<ProbeResult> results)
    {
        if (results.Exists(static result => result.Status == ProbeStatus.Unhealthy))
        {
            return HealthStatus.Unhealthy;
        }

        return results.Exists(static result => result.Status == ProbeStatus.Skipped)
            ? HealthStatus.Degraded
            : HealthStatus.Healthy;
    }
}