using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Описывает адрес PostgreSQL без учётных данных: только хост, порт и имя базы данных.</summary>
public static class ConnectionStringDescriptor
{
    private const string RedactedMarker = "***";

    /// <summary>
    /// Описывает адрес без учётных данных, например <c>postgres:5432/anketa_parser</c>. Пустое значение
    /// обозначается <c>(not configured)</c>, ошибка разбора — <c>(invalid)</c>.
    /// </summary>
    public static string Describe(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "(not configured)";
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return "(invalid)";
        }
    }

    /// <summary>
    /// Маскирует пароль в строке подключения. Это парсер строки подключения, а не фильтр произвольных
    /// сообщений. Для диагностики предпочтителен <see cref="Describe"/>.
    /// </summary>
    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = RedactedMarker;
            }

            return builder.ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return "(invalid)";
        }
    }
}