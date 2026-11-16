namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Предоставляет безопасные сведения о среде выполнения. Инфраструктурная реализация читает состояние
/// процесса, машины и контейнера.
/// </summary>
public interface IRuntimeSnapshotProvider
{
    /// <summary>Читает текущее состояние среды выполнения.</summary>
    RuntimeSnapshot Capture();
}