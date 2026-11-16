namespace AnketaParser.Core;

/// <summary>Стабильные коды завершения Host и CLI. Их значения входят в машинный контракт.</summary>
public static class ExitCodes
{
    /// <summary>Команда или хост успешно завершены.</summary>
    public const int Success = 0;

    /// <summary>Непредвиденная ошибка; безопасная диагностика записана в stderr.</summary>
    public const int UnexpectedError = 1;

    /// <summary>Неверные аргументы командной строки.</summary>
    public const int InvalidUsage = 2;

    /// <summary>Некорректная конфигурация.</summary>
    public const int InvalidConfiguration = 3;

    /// <summary>Настроенная внешняя зависимость недоступна или неисправна.</summary>
    public const int DependencyUnavailable = 4;
}