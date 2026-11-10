namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// A readiness probe for one external dependency. Implementations live in the infrastructure
/// layer; the contract itself knows nothing about PostgreSQL, browsers or other transports.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>Stable, machine-readable probe name.</summary>
    string Name { get; }

    /// <summary>
    /// Checks the dependency. Implementations must not throw for expected dependency failures;
    /// they return <see cref="ProbeStatus.Unhealthy"/> with a secret-free detail instead.
    /// </summary>
    Task<ProbeResult> CheckAsync(CancellationToken cancellationToken);
}