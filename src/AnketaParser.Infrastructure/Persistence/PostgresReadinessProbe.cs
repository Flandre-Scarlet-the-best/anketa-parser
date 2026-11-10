using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

/// <summary>
/// Infrastructure readiness probe for PostgreSQL. It opens a real connection and runs
/// <c>SELECT 1</c>; a connection failure is reported as <see cref="ProbeStatus.Unhealthy"/>
/// and never as a successful status.
/// </summary>
public sealed partial class PostgresReadinessProbe : IReadinessProbe
{
    /// <summary>Stable probe name used by the CLI and the host logs.</summary>
    public const string ProbeName = "postgres";

    private readonly IOptionsMonitor<DatabaseSettings> _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresReadinessProbe> _logger;

    /// <summary>Creates the probe.</summary>
    public PostgresReadinessProbe(
        IOptionsMonitor<DatabaseSettings> settings,
        TimeProvider timeProvider,
        ILogger<PostgresReadinessProbe> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => ProbeName;

    /// <inheritdoc />
    public async Task<ProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.IsConfigured)
        {
            return ProbeResult.Skipped(ProbeName, "Database is not configured; readiness is not claimed.");
        }

        var startedAt = _timeProvider.GetTimestamp();
        var target = ConnectionStringDescriptor.Describe(settings.ConnectionString);

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
            {
                Timeout = settings.ConnectTimeoutSeconds,
                CommandTimeout = settings.CommandTimeoutSeconds,
                ApplicationName = "anketa-parser",
                Pooling = false,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var version = connection.PostgreSqlVersion;
            return ProbeResult.Healthy(
                ProbeName,
                $"PostgreSQL {version} answered at {target}.",
                _timeProvider.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            LogProbeFailure(exception, target);
            return ProbeResult.Unhealthy(
                ProbeName,
                $"PostgreSQL at {target} did not answer ({DescribeFailure(exception)}).",
                _timeProvider.GetElapsedTime(startedAt));
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        // Only a stable classification is reported: Npgsql messages may embed host details, and a
        // probe result must stay free of connection specifics and credentials.
        return exception switch
        {
            NpgsqlException { IsTransient: true } => "transient connection error",
            PostgresException postgres => $"server error {postgres.SqlState}",
            NpgsqlException => "connection error",
            _ => "connection error",
        };
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "PostgreSQL readiness probe failed for target {Target}.")]
    private partial void LogProbeFailure(Exception exception, string target);
}