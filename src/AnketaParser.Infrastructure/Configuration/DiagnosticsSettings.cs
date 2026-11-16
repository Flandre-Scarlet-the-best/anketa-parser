namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Настройки диагностики из секции <c>Diagnostics</c>.</summary>
public sealed class DiagnosticsSettings
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Diagnostics";

    /// <summary>Предельная длительность одной проверки зависимости в секундах.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;
}