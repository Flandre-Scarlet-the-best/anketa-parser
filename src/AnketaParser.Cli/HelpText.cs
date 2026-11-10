namespace AnketaParser.Cli;

/// <summary>Usage text and exit code documentation, kept in one place.</summary>
internal static class HelpText
{
    public static string Usage(string productName, string version)
    {
        return $"""
            {productName} CLI {version}

            Usage:
              anketa-parser-cli <command> [--json]

            Commands:
              doctor    Diagnose configuration, runtime and dependency readiness.
              version   Print the tool version.
              help      Show this help.

            Options:
              --json         Machine-readable mode: exactly one JSON document on stdout,
                             all diagnostics and log lines on stderr.
              -h, --help     Show this help.
              -v, --version  Print the tool version.

            Exit codes:
              0  success, configuration valid, dependencies healthy or not configured
              1  unexpected internal error
              2  invalid command line usage
              3  invalid or missing configuration
              4  a required dependency is unavailable

            Configuration:
              appsettings.json, appsettings.<DOTNET_ENVIRONMENT>.json and appsettings.Local.json
              next to the executable, then environment variables prefixed ANKETAPARSER_
              (nested keys use a double underscore, for example
              ANKETAPARSER_Database__ConnectionString).
            """;
    }
}