using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Host;
using AnketaParser.Infrastructure.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

namespace AnketaParser.Tests;

public sealed class HostApplicationTests
{
    private const string SafeSettings = """
        {
          "Host": {
            "InstanceName": "test-host",
            "ShutdownTimeoutSeconds": 5,
            "StartupDiagnosticsEnabled": true,
            "ReadinessCheckIntervalSeconds": 0
          },
          "Database": { "ConnectionString": "", "Required": false }
        }
        """;

    [Fact]
    public void DependencyGraphBuilds()
    {
        using var root = new Support.TemporaryContentRoot(SafeSettings);

        using var host = HostApplication.CreateHost([], root.Path);

        Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<IReadinessProbe>>(
            host.Services.GetServices<IReadinessProbe>()));
        Assert.NotNull(host.Services.GetRequiredService<DiagnosticsRunner>());
        Assert.NotNull(host.Services.GetRequiredService<ReadinessStateStore>());
    }

    [Fact]
    public async Task HostStartsRunsDiagnosticsAndStopsGracefully()
    {
        using var root = new Support.TemporaryContentRoot(SafeSettings);

        using var host = HostApplication.CreateHost([], root.Path);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var state = host.Services.GetRequiredService<ReadinessStateStore>();
        var report = state.LastReport;
        Assert.NotNull(report);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal("test-host", report.Runtime.InstanceName);

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(stopTimeout.Token);

        Assert.NotNull(state.LastReport);
    }

    [Fact]
    public async Task CancellationStopsHostWithSuccessExitCode()
    {
        using var root = new Support.TemporaryContentRoot(SafeSettings);
        using var cts = new CancellationTokenSource();

        var runTask = HostApplication.RunAsync([], cts.Token, root.Path);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task InvalidConfigurationFailsStartupInsteadOfRunningGreen()
    {
        using var root = new Support.TemporaryContentRoot("""
            { "Host": { "ShutdownTimeoutSeconds": 0 } }
            """);

        var exitCode = await HostApplication
            .RunAsync([], TestContext.Current.CancellationToken, root.Path)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.InvalidConfiguration, exitCode);
    }

    [Fact]
    public async Task RequiredUnreachableDependencyFailsStartup()
    {
        using var root = new Support.TemporaryContentRoot("""
            {
              "Host": { "ShutdownTimeoutSeconds": 5, "ReadinessCheckIntervalSeconds": 0 },
              "Database": {
                "ConnectionString": "Host=127.0.0.1;Port=1;Database=anketa_parser;Username=anketa_parser;Password=super-secret",
                "Required": true,
                "ConnectTimeoutSeconds": 1,
                "CommandTimeoutSeconds": 1
              },
              "Diagnostics": { "ProbeTimeoutSeconds": 10 }
            }
            """);

        var exitCode = await HostApplication
            .RunAsync([], TestContext.Current.CancellationToken, root.Path)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.DependencyUnavailable, exitCode);
    }

    [Fact]
    public async Task OptionalUnreachableDependencyKeepsHostRunning()
    {
        using var root = new Support.TemporaryContentRoot("""
            {
              "Host": { "ShutdownTimeoutSeconds": 5, "ReadinessCheckIntervalSeconds": 0 },
              "Database": {
                "ConnectionString": "Host=127.0.0.1;Port=1;Database=anketa_parser;Username=anketa_parser;Password=super-secret",
                "Required": false,
                "ConnectTimeoutSeconds": 1,
                "CommandTimeoutSeconds": 1
              },
              "Diagnostics": { "ProbeTimeoutSeconds": 10 }
            }
            """);

        using var host = HostApplication.CreateHost([], root.Path);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var report = host.Services.GetRequiredService<ReadinessStateStore>().LastReport;
        Assert.NotNull(report);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(stopTimeout.Token);
    }
}