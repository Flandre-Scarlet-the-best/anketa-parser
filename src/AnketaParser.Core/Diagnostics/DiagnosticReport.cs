namespace AnketaParser.Core.Diagnostics;

/// <summary>Результат диагностики: среда выполнения, источники конфигурации и проверки зависимостей.</summary>
/// <param name="GeneratedAtUtc">Момент формирования отчёта.</param>
/// <param name="Status">Общий статус готовности.</param>
/// <param name="Runtime">Сведения о среде выполнения без секретов.</param>
/// <param name="ConfigurationSources">Источники итоговых настроек.</param>
/// <param name="Probes">Результаты проверок в порядке регистрации.</param>
public sealed record DiagnosticReport(
    DateTimeOffset GeneratedAtUtc,
    HealthStatus Status,
    RuntimeSnapshot Runtime,
    IReadOnlyList<ConfigurationSourceInfo> ConfigurationSources,
    IReadOnlyList<ProbeResult> Probes)
{
    /// <summary>Хотя бы одна настроенная зависимость не прошла проверку.</summary>
    public bool HasFailedDependency => Probes.Any(static probe => probe.Status == ProbeStatus.Unhealthy);
}