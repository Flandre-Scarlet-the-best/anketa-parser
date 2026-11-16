namespace AnketaParser.Core.Diagnostics;

/// <summary>Результат проверки внешней зависимости. <see cref="Detail"/> не должен содержать секретов.</summary>
/// <param name="Name">Стабильное имя проверки, например <c>postgres</c>.</param>
/// <param name="Status">Готовность, отказ или пропуск проверки зависимости.</param>
/// <param name="Detail">Описание результата без секретов.</param>
/// <param name="Duration">Длительность проверки.</param>
public sealed record ProbeResult(string Name, ProbeStatus Status, string Detail, TimeSpan Duration)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static ProbeResult Healthy(string name, string detail, TimeSpan duration) =>
        new(name, ProbeStatus.Healthy, detail, duration);

    /// <summary>Создаёт результат отказа.</summary>
    public static ProbeResult Unhealthy(string name, string detail, TimeSpan duration) =>
        new(name, ProbeStatus.Unhealthy, detail, duration);

    /// <summary>Создаёт результат пропущенной проверки с нулевой длительностью.</summary>
    public static ProbeResult Skipped(string name, string detail) =>
        new(name, ProbeStatus.Skipped, detail, TimeSpan.Zero);
}