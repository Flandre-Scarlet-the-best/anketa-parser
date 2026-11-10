namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Secret-free description of the runtime the process is executing in.
/// </summary>
/// <param name="FrameworkDescription">.NET runtime description, for example <c>.NET 10.0.14</c>.</param>
/// <param name="OsDescription">Operating system description.</param>
/// <param name="Architecture">Process architecture.</param>
/// <param name="IsContainer">Whether the process looks like it runs inside a container.</param>
/// <param name="MachineName">Machine name or container host name.</param>
/// <param name="ProcessId">Current process identifier.</param>
/// <param name="Uptime">Time elapsed since the host process started, when known.</param>
/// <param name="InstanceName">Configured instance name used to tell hosts apart in logs.</param>
/// <param name="EnvironmentName">Effective hosting environment name.</param>
public sealed record RuntimeSnapshot(
    string FrameworkDescription,
    string OsDescription,
    string Architecture,
    bool IsContainer,
    string MachineName,
    int ProcessId,
    TimeSpan? Uptime,
    string InstanceName,
    string EnvironmentName);