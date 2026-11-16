namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Обязательная зависимость недоступна. Исполняемое приложение преобразует ошибку в <see
/// cref="ExitCodes.DependencyUnavailable"/>.
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    /// <summary>Создаёт исключение с безопасным сообщением.</summary>
    public DependencyUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с безопасным сообщением и исходной причиной.</summary>
    public DependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}