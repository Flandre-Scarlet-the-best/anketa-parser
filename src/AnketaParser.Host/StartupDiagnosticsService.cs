using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnketaParser.Host;

/// <summary>
/// Runs infrastructure diagnostics once during startup, logs a secret-free report and records the
/// aggregate status. When <c>Database:Required</c> is enabled, an unhealthy dependency aborts startup
/// instead of letting the process pretend to be ready.
/// </summary>
internal sealed partial class StartupDiagnosticsService : IHostedService
{
    private readonly DiagnosticsRunner _runner;
    private readonly IOptionsMonitor<HostSettings> _hostSettings;
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;
    private readonly IOptionsMonitor<DiagnosticsSettings> _diagnosticsSettings;
    private readonly ConfigurationSourceSet _configurationSources;
    private readonly ReadinessStateStore _state;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StartupDiagnosticsService> _logger;

    public StartupDiagnosticsService(
        DiagnosticsRunner runner,
        IOptionsMonitor<HostSettings> hostSettings,
        IOptionsMonitor<DatabaseSettings> databaseSettings,
        IOptionsMonitor<DiagnosticsSettings> diagnosticsSettings,
        ConfigurationSourceSet configurationSources,
        ReadinessStateStore state,
        IHostApplicationLifetime lifetime,
        ILogger<StartupDiagnosticsService> logger)
    {
        _runner = runner;
        _hostSettings = hostSettings;
        _databaseSettings = databaseSettings;
        _diagnosticsSettings = diagnosticsSettings;
        _configurationSources = configurationSources;
        _state = state;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _hostSettings.CurrentValue;
        if (!settings.StartupDiagnosticsEnabled)
        {
            LogDiagnosticsDisabled();
            return;
        }

        var report = await _runner
            .RunAsync(
                _configurationSources.Sources,
                TimeSpan.FromSeconds(_diagnosticsSettings.CurrentValue.ProbeTimeoutSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        _state.Update(report);

        LogDiagnosticsCompleted(
            report.Status,
            report.Runtime.InstanceName,
            report.Runtime.EnvironmentName,
            report.Runtime.ProcessId,
            report.Runtime.IsContainer);

        foreach (var probe in report.Probes)
        {
            var level = probe.Status == ProbeStatus.Unhealthy ? LogLevel.Error : LogLevel.Information;
            LogProbeResult(
                level,
                probe.Name,
                probe.Status,
                probe.Duration,
                probe.Detail);
        }

        if (!report.HasFailedDependency)
        {
            return;
        }

        var failures = string.Join(
            "; ",
            report.Probes
                .Where(static probe => probe.Status == ProbeStatus.Unhealthy)
                .Select(static probe => $"{probe.Name}: {probe.Detail}"));

        if (_databaseSettings.CurrentValue.Required)
        {
            LogRequiredDependencyUnavailable(failures);
            _lifetime.StopApplication();
            throw new DependencyUnavailableException($"Required dependency is unavailable. {failures}");
        }

        LogDegradedState(failures);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Startup diagnostics are disabled (Host:StartupDiagnosticsEnabled = false); readiness is not verified.")]
    private partial void LogDiagnosticsDisabled();

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Infrastructure diagnostics completed with status {Status} (instance {InstanceName}, environment {EnvironmentName}, pid {ProcessId}, container {IsContainer}).")]
    private partial void LogDiagnosticsCompleted(
        HealthStatus status,
        string instanceName,
        string environmentName,
        int processId,
        bool isContainer);

    [LoggerMessage(
        EventId = 1003,
        Message = "Probe {ProbeName} reported {ProbeStatus} in {Elapsed}: {ProbeDetail}")]
    private partial void LogProbeResult(LogLevel level, string probeName, ProbeStatus probeStatus, TimeSpan elapsed, string probeDetail);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Critical,
        Message = "A required dependency is unavailable; the host stops instead of reporting a false ready state. {Failures}")]
    private partial void LogRequiredDependencyUnavailable(string failures);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Dependency check failed but Database:Required is false, so the host keeps running in a degraded state. {Failures}")]
    private partial void LogDegradedState(string failures);
}