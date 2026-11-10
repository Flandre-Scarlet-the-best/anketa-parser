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
        Assert.Contains("did not finish", probe.Detail, StringComparison.Ordinal);
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
}