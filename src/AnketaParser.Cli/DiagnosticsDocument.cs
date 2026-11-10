using System.Text.Json.Serialization;

namespace AnketaParser.Cli;

/// <summary>
/// Stable machine-readable contract of the CLI. Field names and semantics are part of the public
/// interface and are covered by tests; <see cref="SchemaVersion"/> is bumped on breaking changes.
/// </summary>
internal sealed record DiagnosticsDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("toolVersion")]
    public required string ToolVersion { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("runtime")]
    public RuntimeDocument? Runtime { get; init; }

    [JsonPropertyName("configuration")]
    public required ConfigurationDocument Configuration { get; init; }

    [JsonPropertyName("probes")]
    public required IReadOnlyList<ProbeDocument> Probes { get; init; }

    [JsonPropertyName("errors")]
    public required IReadOnlyList<string> Errors { get; init; }
}

internal sealed record RuntimeDocument
{
    [JsonPropertyName("framework")]
    public required string Framework { get; init; }

    [JsonPropertyName("os")]
    public required string Os { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("isContainer")]
    public required bool IsContainer { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public double? UptimeSeconds { get; init; }

    [JsonPropertyName("instanceName")]
    public required string InstanceName { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }
}

internal sealed record ConfigurationDocument
{
    [JsonPropertyName("valid")]
    public required bool Valid { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<ConfigurationSourceDocument> Sources { get; init; }

    [JsonPropertyName("database")]
    public required DatabaseDocument Database { get; init; }
}

internal sealed record ConfigurationSourceDocument
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

internal sealed record DatabaseDocument
{
    [JsonPropertyName("configured")]
    public required bool Configured { get; init; }

    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("connectTimeoutSeconds")]
    public required int ConnectTimeoutSeconds { get; init; }
}

internal sealed record ProbeDocument
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    [JsonPropertyName("durationMs")]
    public required double DurationMs { get; init; }
}