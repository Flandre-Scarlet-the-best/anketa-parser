namespace AnketaParser.Cli;

/// <summary>Разбирает одну необязательную команду и глобальный флаг <c>--json</c> без сторонних зависимостей.</summary>
internal static class CommandLineParser
{
    public static CliInvocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var isJson = args.Contains("--json", StringComparer.Ordinal);
        var command = CliCommand.Help;
        var commandSeen = false;

        foreach (var arg in args)
        {
            if (commandSeen && arg is "-h" or "--help" or "help" or "-v" or "--version" or "version" or "doctor")
            {
                return CliInvocation.Invalid("Допускается только одна команда.", isJson);
            }

            switch (arg)
            {
                case "--json":
                    continue;
                case "-h" or "--help" or "help":
                    command = CliCommand.Help;
                    commandSeen = true;
                    continue;
                case "-v" or "--version" or "version":
                    command = CliCommand.Version;
                    commandSeen = true;
                    continue;
                case "doctor":
                    command = CliCommand.Doctor;
                    commandSeen = true;
                    continue;
            }

            if (arg.StartsWith('-'))
            {
                return CliInvocation.Invalid("Неизвестный параметр. См. --help.", isJson);
            }

            if (commandSeen)
            {
                return CliInvocation.Invalid("Лишний аргумент. См. --help.", isJson);
            }

            return CliInvocation.Invalid("Неизвестная команда. См. --help.", isJson);
        }

        return new CliInvocation(command, isJson, Error: null);
    }
}