namespace AnketaParser.Cli;

/// <summary>Supported CLI commands.</summary>
internal enum CliCommand
{
    /// <summary>Show usage information.</summary>
    Help,

    /// <summary>Print the tool version.</summary>
    Version,

    /// <summary>Diagnose configuration, runtime and dependencies.</summary>
    Doctor,
}

/// <summary>
/// Parsed command line. Invalid input is represented instead of thrown so that the CLI can answer with
/// a documented exit code and, in machine-readable mode, a single JSON error document.
/// </summary>
/// <param name="Command">Selected command.</param>
/// <param name="IsMachineReadable">Whether <c>--json</c> was requested.</param>
/// <param name="Error">Usage error message, when the input was invalid.</param>
internal sealed record CliInvocation(CliCommand Command, bool IsMachineReadable, string? Error)
{
    public bool IsValid => Error is null;

    public static CliInvocation Invalid(string error, bool isMachineReadable) =>
        new(CliCommand.Help, isMachineReadable, error);
}