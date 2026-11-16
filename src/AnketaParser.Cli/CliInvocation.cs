namespace AnketaParser.Cli;

/// <summary>Поддерживаемые команды CLI.</summary>
internal enum CliCommand
{
    /// <summary>Показать справку.</summary>
    Help,

    /// <summary>Показать версию инструмента.</summary>
    Version,

    /// <summary>Проверить конфигурацию, среду выполнения и зависимости.</summary>
    Doctor,
}

/// <summary>
/// Результат разбора аргументов. Ошибка представлена данными для возврата документированного кода и
/// единственного JSON-документа в машинном режиме.
/// </summary>
/// <param name="Command">Выбранная команда.</param>
/// <param name="IsMachineReadable">Признак флага <c>--json</c>.</param>
/// <param name="Error">Сообщение об ошибке аргументов.</param>
internal sealed record CliInvocation(CliCommand Command, bool IsMachineReadable, string? Error)
{
    public bool IsValid => Error is null;

    public static CliInvocation Invalid(string error, bool isMachineReadable) =>
        new(CliCommand.Help, isMachineReadable, error);
}