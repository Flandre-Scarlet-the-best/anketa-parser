namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Проверка одной внешней зависимости. Реализации принадлежат инфраструктурному слою; контракт не
/// зависит от транспорта.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>Стабильное машинное имя проверки.</summary>
    string Name { get; }

    /// <summary>
    /// Проверяет зависимость. Ожидаемые отказы возвращаются как <see cref="ProbeStatus.Unhealthy"/> с
    /// безопасным описанием.
    /// </summary>
    Task<ProbeResult> CheckAsync(CancellationToken cancellationToken);
}