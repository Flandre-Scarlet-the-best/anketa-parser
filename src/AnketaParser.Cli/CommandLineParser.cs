namespace AnketaParser.Cli;

/// <summary>
/// Minimal, dependency-free argument parser. It understands one optional command and the global
/// <c>--json</c> flag; everything else is a documented usage error.
/// </summary>
internal static class CommandLineParser
{
    public static CliInvocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var isJson = false;
        var command = CliCommand.Help;
        var commandSeen = false;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json":
                    isJson = true;
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
                return CliInvocation.Invalid($"Unknown option '{arg}'.", isJson);
            }

            if (commandSeen)
            {
                return CliInvocation.Invalid($"Unexpected argument '{arg}'.", isJson);
            }

            return CliInvocation.Invalid($"Unknown command '{arg}'.", isJson);
        }

        return new CliInvocation(command, isJson, Error: null);
    }
}