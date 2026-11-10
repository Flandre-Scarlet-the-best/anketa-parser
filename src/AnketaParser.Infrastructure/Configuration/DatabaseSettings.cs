namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// PostgreSQL connection settings. Bound from the <c>Database</c> configuration section.
/// The connection string is a secret: it must come from environment variables or a local file
/// that is excluded from Git, and it is never logged or printed by diagnostics.
/// </summary>
public sealed class DatabaseSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// PostgreSQL connection string. Empty means the database integration is not configured,
    /// which is a valid state for this stage of the project.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// When true, an unreachable database makes host startup fail instead of merely logging a warning.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>TCP connect timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Command timeout in seconds used by the readiness probe.</summary>
    public int CommandTimeoutSeconds { get; set; } = 5;

    /// <summary>True when a non-empty connection string is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}