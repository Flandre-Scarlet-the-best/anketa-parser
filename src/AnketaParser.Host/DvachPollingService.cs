using AnketaParser.Infrastructure.Sources;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnketaParser.Host;

internal sealed partial class DvachPollingService(
    DvachIngestion ingestion, TimeProvider clock, ILogger<DvachPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ingestion.IsEnabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ingestion.PollInterval;
            try
            {
                var report = await ingestion.ScanAsync(fixturePath: null, stoppingToken).ConfigureAwait(false);
                if (report.Status == "complete")
                    LogComplete(logger, report.Metrics!.Received, report.Metrics.Candidates, report.Metrics.ChangedPosts);
                else if (report.Status is not ("busy" or "backoff"))
                    LogFailure(logger, report.Status, report.Phase, report.Failure);

                var remaining = report.RetryAt - clock.GetUtcNow();
                if (remaining > delay)
                    delay = TimeSpan.FromSeconds(Math.Min(86400, remaining.Value.TotalSeconds));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                LogFailure(logger, "internal_error", "scan", error.GetType().Name);
            }

            await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information,
        Message = "Сверка Двача завершена. Получено {Received}, кандидатов {Candidates}, изменений {Changed}.")]
    private static partial void LogComplete(ILogger logger, int received, int candidates, int changed);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "Сверка Двача завершилась со статусом {Status} на фазе {Phase}. Категория {Failure}.")]
    private static partial void LogFailure(ILogger logger, string status, string phase, string? failure);
}