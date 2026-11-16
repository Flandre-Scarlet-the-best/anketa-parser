using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Tests;

public sealed class DiagnosticsRunnerTests
{
    private static readonly IReadOnlyList<ConfigurationSourceInfo> NoSources = [];

    private static DiagnosticsRunner CreateRunner(params IReadinessProbe[] probes) =>
        new(probes, new Support.StubRuntimeSnapshotProvider(), TimeProvider.System);

    [Fact]
    public async Task HealthyProbesProduceHealthyReport()
    {
        var runner = CreateRunner(
            new Support.StubProbe("alpha", _ => Task.FromResult(ProbeResult.Healthy("alpha", "ok", TimeSpan.Zero))),
            new Support.StubProbe("beta", _ => Task.FromResult(ProbeResult.Healthy("beta", "ok", TimeSpan.Zero))));

        var report = await runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.False(report.HasFailedDependency);
        Assert.Equal(2, report.Probes.Count);
    }

    [Fact]
    public async Task SkippedProbeDegradesStatusWithoutFailingReadiness()
    {
        var runner = CreateRunner(
            new Support.StubProbe("alpha", _ => Task.FromResult(ProbeResult.Skipped("alpha", "not configured"))));

        var report = await runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.False(report.HasFailedDependency);
    }

    [Fact]
    public async Task UnhealthyProbeIsAggregatedAsUnhealthy()
    {
        var runner = CreateRunner(
            new Support.StubProbe("alpha", _ => Task.FromResult(ProbeResult.Healthy("alpha", "ok", TimeSpan.Zero))),
            new Support.StubProbe("beta", _ => Task.FromResult(ProbeResult.Unhealthy("beta", "down", TimeSpan.Zero))));

        var report = await runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.True(report.HasFailedDependency);
    }

    [Fact]
    public async Task ThrowingProbeBecomesUnhealthyWithoutLeakingExceptionMessage()
    {
        const string secret = "Host=db;Password=super-secret";
        var runner = CreateRunner(
            new Support.StubProbe("alpha", _ => throw new InvalidOperationException(secret)));

        var report = await runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var probe = Assert.Single(report.Probes);
        Assert.Equal(ProbeStatus.Unhealthy, probe.Status);
        Assert.DoesNotContain("super-secret", probe.Detail, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HangingProbeIsReportedAsTimeout()
    {
        var runner = CreateRunner(
            new Support.StubProbe("slow", async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return ProbeResult.Healthy("slow", "ok", TimeSpan.Zero);
            }));

        var report = await runner.RunAsync(
            NoSources,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);

        var probe = Assert.Single(report.Probes);
        Assert.Equal(ProbeStatus.Unhealthy, probe.Status);
        Assert.Contains("не завершена", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var runner = CreateRunner(
            new Support.StubProbe("slow", async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return ProbeResult.Healthy("slow", "ok", TimeSpan.Zero);
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(NoSources, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task InjectedTimeControlsTimeoutDurationAndReportTimestamp()
    {
        var clock = new Support.ManualTimeProvider();
        var runner = new DiagnosticsRunner(
            [new Support.StubProbe("slow", async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            })], new Support.StubRuntimeSnapshotProvider(), clock);

        var pending = runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Probes);
        Assert.Equal(ProbeStatus.Unhealthy, result.Status);
        Assert.Contains("не завершена", result.Detail, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(5), report.GeneratedAtUtc);
    }

    [Fact]
    public async Task InjectedTimeMeasuresUnexpectedFailure()
    {
        var clock = new Support.ManualTimeProvider();
        var runner = new DiagnosticsRunner(
            [new Support.StubProbe("failed", _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("secret-sentinel");
            })], new Support.StubRuntimeSnapshotProvider(), clock);

        var result = Assert.Single((await runner.RunAsync(NoSources, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).Probes);

        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
        Assert.Contains("InvalidOperationException", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-sentinel", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedCancellationIsFailureRatherThanTimeout()
    {
        var runner = CreateRunner(new Support.StubProbe("failed", _ => throw new OperationCanceledException("secret-sentinel")));
        var result = Assert.Single((await runner.RunAsync(NoSources, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).Probes);

        Assert.Equal(ProbeStatus.Unhealthy, result.Status);
        Assert.Contains("OperationCanceledException", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-sentinel", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationWinsWhenProbeDeadlineAlsoExpires()
    {
        var clock = new Support.ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var runner = new DiagnosticsRunner(
            [new Support.StubProbe("slow", async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            })], new Support.StubRuntimeSnapshotProvider(), clock);

        var pending = runner.RunAsync(NoSources, TimeSpan.FromSeconds(5), caller.Token);
        await caller.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}