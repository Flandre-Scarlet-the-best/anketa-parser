using System.Runtime.InteropServices;

using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Diagnostics;

/// <summary>
/// Reads process, machine and container state for diagnostics. Never touches configuration
/// values that could be secret.
/// </summary>
public sealed class RuntimeSnapshotProvider : IRuntimeSnapshotProvider
{
    private readonly EnvironmentDescriptor _environment;
    private readonly IOptionsMonitor<HostSettings> _hostSettings;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the provider.</summary>
    public RuntimeSnapshotProvider(
        EnvironmentDescriptor environment,
        IOptionsMonitor<HostSettings> hostSettings,
        TimeProvider timeProvider)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _hostSettings = hostSettings ?? throw new ArgumentNullException(nameof(hostSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public RuntimeSnapshot Capture()
    {
        return new RuntimeSnapshot(
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OsDescription: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            IsContainer: IsRunningInContainer(),
            MachineName: Environment.MachineName,
            ProcessId: Environment.ProcessId,
            Uptime: TryGetUptime(),
            InstanceName: _hostSettings.CurrentValue.InstanceName,
            EnvironmentName: _environment.Name);
    }

    private TimeSpan? TryGetUptime()
    {
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var started = new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var uptime = _timeProvider.GetUtcNow() - started;
            return uptime < TimeSpan.Zero ? null : uptime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsRunningInContainer()
    {
        var marker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists("/.dockerenv");
    }
}