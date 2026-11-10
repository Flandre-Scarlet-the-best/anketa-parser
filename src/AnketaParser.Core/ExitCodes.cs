namespace AnketaParser.Core;

/// <summary>
/// Stable process exit codes shared by the long-running host and the agent CLI.
/// The values are part of the machine-readable contract and must not change silently.
/// </summary>
public static class ExitCodes
{
    /// <summary>The command or host finished successfully.</summary>
    public const int Success = 0;

    /// <summary>An unexpected error occurred; details are written to stderr.</summary>
    public const int UnexpectedError = 1;

    /// <summary>Command line arguments were missing, unknown or malformed.</summary>
    public const int InvalidUsage = 2;

    /// <summary>Configuration is missing or invalid.</summary>
    public const int InvalidConfiguration = 3;

    /// <summary>A required external dependency is unavailable or unhealthy.</summary>
    public const int DependencyUnavailable = 4;
}