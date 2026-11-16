using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnketaParser.Host;

/// <summary>
/// Периодически проверяет готовность при настроенной БД и положительном интервале. Без зависимости
/// фоновый цикл не запускается.
/// </summary>
internal sealed partial class ReadinessMonitorService : BackgroundService
{
    private readonly DiagnosticsRunner _runner;
    private readonly IOptionsMonitor<HostSettings> _hostSettings;
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;
    private readonly IOptionsMonitor<DiagnosticsSettings> _diagnosticsSettings;
    private readonly ConfigurationSourceSet _configurationSources;
    private readonly ReadinessStateStore _state;
    private readonly ILogger<ReadinessMonitorService> _logger;

    public ReadinessMonitorService(
        DiagnosticsRunner runner,
        IOptionsMonitor<HostSettings> hostSettings,
        IOptionsMonitor<DatabaseSettings> databaseSettings,
        IOptionsMonitor<DiagnosticsSettings> diagnosticsSettings,
        ConfigurationSourceSet configurationSources,
        ReadinessStateStore state,
        ILogger<ReadinessMonitorService> logger)
    {
        _runner = runner;
        _hostSettings = hostSettings;
        _databaseSettings = databaseSettings;
        _diagnosticsSettings = diagnosticsSettings;
        _configurationSources = configurationSources;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _hostSettings.CurrentValue.ReadinessCheckIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            LogMonitoringDisabled();
            return;
        }

        if (!_databaseSettings.CurrentValue.IsConfigured)
        {
            LogMonitoringIdle();
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var probeTimeout = TimeSpan.FromSeconds(_diagnosticsSettings.CurrentValue.ProbeTimeoutSeconds);
        using var timer = new PeriodicTimer(interval);
        var lastStatus = _state.LastReport?.Status;

        LogMonitoringStarted(intervalSeconds);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var report = await _runner
                    .RunAsync(_configurationSources.Sources, probeTimeout, stoppingToken)
                    .ConfigureAwait(false);
                _state.Update(report);

                if (report.Status != lastStatus)
                {
                    LogReadinessChanged(lastStatus, report.Status);
                    lastStatus = report.Status;
                }
                else
                {
                    LogReadinessUnchanged(report.Status);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ожидаемая отмена при штатной остановке.
        }

        LogMonitoringStopped();
    }


    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Периодический мониторинг готовности отключён (Host:ReadinessCheckIntervalSeconds = 0).")]
    private partial void LogMonitoringDisabled();

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Периодический мониторинг готовности не нужен: внешняя зависимость не настроена.")]
    private partial void LogMonitoringIdle();

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = "Периодический мониторинг готовности запущен с интервалом {IntervalSeconds} с.")]
    private partial void LogMonitoringStarted(int intervalSeconds);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Готовность изменилась с {PreviousStatus} на {CurrentStatus}.")]
    private partial void LogReadinessChanged(HealthStatus? previousStatus, HealthStatus currentStatus);



    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Debug,
        Message = "Проверка готовности завершена; статус прежний: {CurrentStatus}.")]
    private partial void LogReadinessUnchanged(HealthStatus currentStatus);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Information,
        Message = "Периодический мониторинг готовности остановлен.")]
    private partial void LogMonitoringStopped();
}