using AnketaParser.Core.Diagnostics;

using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class BackupReadinessProbe(IOptions<BackupSettings> options) : IReadinessProbe
{
    public string Name => "backup";
    public Task<ProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        var service = new BackupService(settings);
        BackupState state;
        try { state = service.Status(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Task.FromResult(ProbeResult.Skipped(Name, "Состояние резервирования недоступно или повреждено."));
        }
        var age = state.LastSuccess is null ? (double?)null : (DateTimeOffset.UtcNow - state.LastSuccess.Value).TotalHours;
        var verifiedAge = state.LastRestoreVerified is null ? (double?)null : (DateTimeOffset.UtcNow - state.LastRestoreVerified.Value).TotalHours;
        var detail = $"Последняя копия: {state.LastSuccess:O}; последний отказ: {state.LastFailure:O}; категория: {state.FailureCategory ?? "нет"}; возраст, ч: {age:0.0}; проверка восстановлением: {state.LastRestoreVerified:O}; возраст проверки, ч: {verifiedAge:0.0}.";
        return Task.FromResult(!service.IsCurrentArchiveAvailable(state) || state.LastSuccess is null || age > settings.IntervalSeconds / 3600.0 * 2 ||
            state.LastFailure > state.LastSuccess || state.LastRestoreVerified is null || verifiedAge > settings.IntervalSeconds / 3600.0 * 2
            ? ProbeResult.Skipped(Name, detail) : ProbeResult.Healthy(Name, detail, TimeSpan.Zero));
    }
}