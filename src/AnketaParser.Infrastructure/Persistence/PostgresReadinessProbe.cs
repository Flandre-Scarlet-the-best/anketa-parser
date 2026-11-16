using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Persistence;

/// <summary>
/// Открывает реальное подключение PostgreSQL и выполняет <c>SELECT 1</c>. Отказ подключения
/// возвращается как <see cref="ProbeStatus.Unhealthy"/>.
/// </summary>
public sealed partial class PostgresReadinessProbe : IReadinessProbe
{
    /// <summary>Стабильное имя проверки для CLI и логов хоста.</summary>
    public const string ProbeName = "postgres";

    private readonly IOptionsMonitor<DatabaseSettings> _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresReadinessProbe> _logger;

    /// <summary>Создаёт проверку готовности.</summary>
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
            return ProbeResult.Skipped(ProbeName, "База данных не настроена; готовность не подтверждена.");
        }

        var startedAt = _timeProvider.GetTimestamp();
        var target = ConnectionStringDescriptor.Describe(settings.ConnectionString);

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
            {
                Timeout = settings.ConnectTimeoutSeconds,
                CommandTimeout = settings.CommandTimeoutSeconds,
                ApplicationName = AnketaParser.Core.ApplicationInfo.DefaultProductName,
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
                $"PostgreSQL {version} ответил по адресу {target}.",
                _timeProvider.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            LogProbeFailure(target, DescribeFailure(exception));
            return ProbeResult.Unhealthy(
                ProbeName,
                $"PostgreSQL по адресу {target} не ответил ({DescribeFailure(exception)}).",
                _timeProvider.GetElapsedTime(startedAt));
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        // Сообщения Npgsql могут содержать учётные данные; возвращаем только категорию отказа.
        return exception switch
        {
            NpgsqlException { IsTransient: true } => "временная ошибка подключения",
            PostgresException postgres => $"ошибка сервера {postgres.SqlState}",
            NpgsqlException => "ошибка подключения",
            _ => "ошибка подключения",
        };
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Проверка готовности PostgreSQL для {Target} завершилась отказом ({FailureCategory}).")]
    private partial void LogProbeFailure(string target, string failureCategory);
}