using AnketaParser.Core.Diagnostics;

using Microsoft.Extensions.Configuration;

namespace AnketaParser.Infrastructure.Persistence;

public sealed class BackupReadinessProbe(IConfiguration configuration) : IReadinessProbe
{
    public string Name => "backup";
    public Task<ProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = configuration.GetSection("Backup").Get<BackupSettings>() ?? new();
        var state = new BackupService(settings).Status();
        var age = state.LastSuccess is null ? (double?)null : (DateTimeOffset.UtcNow - state.LastSuccess.Value).TotalHours;
        var verifiedAge = state.LastRestoreVerified is null ? (double?)null : (DateTimeOffset.UtcNow - state.LastRestoreVerified.Value).TotalHours;
        var detail = $"Последняя копия: {state.LastSuccess:O}; последний отказ: {state.LastFailure:O}; категория: {state.FailureCategory ?? "нет"}; возраст, ч: {age:0.0}; проверка восстановлением: {state.LastRestoreVerified:O}; возраст проверки, ч: {verifiedAge:0.0}.";
        return Task.FromResult(state.LastSuccess is null || age > settings.IntervalSeconds / 3600.0 * 2 ||
            state.LastFailure > state.LastSuccess || state.LastRestoreVerified is null || verifiedAge > settings.IntervalSeconds / 3600.0 * 2
            ? ProbeResult.Skipped(Name, detail) : ProbeResult.Healthy(Name, detail, TimeSpan.Zero));
    }
}