namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Настройки долгоживущего процесса из секции <c>Host</c>.</summary>
public sealed class HostSettings
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Host";

    /// <summary>Логическое имя экземпляра для различения хостов в логах.</summary>
    public string InstanceName { get; set; } = "anketa-parser";

    /// <summary>Допустимая длительность штатной остановки в секундах.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 20;

    /// <summary>Начальная диагностика зависимостей; включена по умолчанию.</summary>
    public bool StartupDiagnosticsEnabled { get; set; } = true;

    /// <summary>Интервал проверок готовности в секундах. Ноль отключает мониторинг.</summary>
    public int ReadinessCheckIntervalSeconds { get; set; } = 60;
}