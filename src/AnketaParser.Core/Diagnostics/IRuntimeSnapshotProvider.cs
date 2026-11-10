namespace AnketaParser.Core.Diagnostics;

/// <summary>
/// Provides a secret-free snapshot of the current runtime. Implemented in the infrastructure
/// layer because it reads process, machine and container state.
/// </summary>
public interface IRuntimeSnapshotProvider
{
    /// <summary>Captures the current runtime state.</summary>
    RuntimeSnapshot Capture();
}