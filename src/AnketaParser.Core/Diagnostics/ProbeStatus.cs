namespace AnketaParser.Core.Diagnostics;

/// <summary>Результат одной проверки готовности.</summary>
public enum ProbeStatus
{
    /// <summary>Зависимость ответила и подтвердила готовность.</summary>
    Healthy,

    /// <summary>Настроенная зависимость не ответила или сообщила об отказе.</summary>
    Unhealthy,

    /// <summary>Зависимость намеренно не настроена; готовность не подтверждена.</summary>
    Skipped,
}