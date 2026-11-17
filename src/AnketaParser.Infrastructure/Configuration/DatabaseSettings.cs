namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Настройки PostgreSQL из секции <c>Database</c>. Секретная строка подключения поступает из окружения
/// или локального файла, исключённого из Git; диагностика её не выводит.
/// </summary>
public sealed class DatabaseSettings
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Строка подключения PostgreSQL. Пустое значение допустимо для необязательной ненастроенной
    /// зависимости.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Отдельный пароль, переопределяющий значение Password в строке подключения.</summary>
    public string? Password { get; set; }

    /// <summary>При true недоступная БД прерывает запуск хоста.</summary>
    public bool Required { get; set; }

    /// <summary>Таймаут подключения TCP в секундах.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Таймаут команды проверки готовности в секундах.</summary>
    public int CommandTimeoutSeconds { get; set; } = 5;

    /// <summary>Признак непустой строки подключения.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}