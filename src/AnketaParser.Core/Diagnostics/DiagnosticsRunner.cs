namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Выполняет зарегистрированные проверки и формирует <see cref="DiagnosticReport"/>. Использует
/// контракт <see cref="IReadinessProbe"/> для общей диагностики Host и CLI.
/// </summary>
public sealed class DiagnosticsRunner
{
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly IRuntimeSnapshotProvider _runtime;
    private readonly TimeProvider _timeProvider;

    /// <summary>Создаёт агрегатор для заданных проверок.</summary>
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

    /// <summary>Проверки в порядке регистрации.</summary>
    public IReadOnlyList<IReadinessProbe> Probes => _probes;

    /// <summary>
    /// Последовательно проверяет зависимости с отдельным таймаутом для каждой. Отказы дают <see
    /// cref="ProbeStatus.Unhealthy"/>; отмена вызывающего кода распространяется наружу.
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

    private async Task<ProbeResult> RunProbeAsync(
        IReadinessProbe probe,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(probeTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await probe.CheckAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unhealthy(
                probe.Name,
                $"Проверка не завершена за {probeTimeout.TotalSeconds:0.###} с.",
                _timeProvider.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // При отмене вызывающим кодом результат проверки не имеет смысла.
            throw;
        }
        catch (Exception exception)
        {
            // Текст исключения может содержать строку подключения или учётные данные.
            return ProbeResult.Unhealthy(
                probe.Name,
                $"Ошибка проверки: {exception.GetType().Name}.",
                _timeProvider.GetElapsedTime(startedAt));
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