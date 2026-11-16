namespace AnketaParser.Core.Diagnostics;

/// <summary>Общая готовность процесса и зависимостей. Работающий процесс сам по себе не доказывает готовность.</summary>
public enum HealthStatus
{
    /// <summary>Все проверенные зависимости успешно ответили.</summary>
    Healthy,

    /// <summary>Необязательная зависимость не настроена или проверка пропущена.</summary>
    Degraded,

    /// <summary>Хотя бы одна настроенная зависимость не прошла проверку.</summary>
    Unhealthy,
}