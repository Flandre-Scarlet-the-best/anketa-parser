using AnketaParser.Infrastructure.Configuration;

namespace AnketaParser.Tests.Support;

/// <summary>
/// Временно убирает переменные окружения приложения: конвейер конфигурации читает
/// <c>ANKETAPARSER_*</c> после JSON-файлов, поэтому значения машины разработчика
/// подменяют настройки теста и могут направить его в реальную базу.
/// Изменение состояния процесса требует исключительного исполнения: тесты,
/// использующие область, входят в коллекцию окружения процесса.
/// </summary>
internal sealed class ProcessEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    private ProcessEnvironment()
    {
    }

    /// <summary>Убирает переменные окружения приложения до освобождения области.</summary>
    public static ProcessEnvironment Isolate()
    {
        var scope = new ProcessEnvironment();
        foreach (var key in Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (!key.StartsWith(AnketaParserConfiguration.EnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase) &&
                !key.Equals(AnketaParserConfiguration.EnvironmentNameVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scope._saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }

        return scope;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);
    }
}