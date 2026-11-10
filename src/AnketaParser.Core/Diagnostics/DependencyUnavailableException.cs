namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Raised when a dependency declared as required is unavailable and the process must not pretend
/// to be ready. Executable hosts map it to <see cref="ExitCodes.DependencyUnavailable"/>.
/// </summary>
public sealed class DependencyUnavailableException : Exception
{
    /// <summary>Creates the exception with a secret-free message.</summary>
    public DependencyUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a secret-free message and an inner cause.</summary>
    public DependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}