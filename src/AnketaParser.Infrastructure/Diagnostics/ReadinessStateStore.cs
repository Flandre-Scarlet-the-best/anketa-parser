using AnketaParser.Core.Diagnostics;

namespace AnketaParser.Infrastructure.Diagnostics;

/// <summary>Хранит последний общий отчёт о готовности для чтения без повторного запуска проверок.</summary>
public sealed class ReadinessStateStore
{
    private DiagnosticReport? _lastReport;

    /// <summary>Последний отчёт либо <see langword="null"/> до первой проверки.</summary>
    public DiagnosticReport? LastReport => Volatile.Read(ref _lastReport);

    /// <summary>Заменяет сохранённый отчёт.</summary>
    public void Update(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Volatile.Write(ref _lastReport, report);
    }
}