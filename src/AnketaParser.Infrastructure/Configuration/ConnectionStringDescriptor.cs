using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Turns a PostgreSQL connection string into a secret-free descriptor.
/// Only the host, port, database name and user name are exposed; the password never leaves
/// <see cref="NpgsqlConnectionStringBuilder"/>.
/// </summary>
public static class ConnectionStringDescriptor
{
    private const string RedactedMarker = "***";

    /// <summary>
    /// Describes the configured target without credentials, for example <c>postgres:5432/anketa_parser</c>.
    /// Returns <c>(not configured)</c> for an empty value and <c>(invalid)</c> when parsing fails.
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
    /// Returns the connection string with its password removed. Use it only when a full string is
    /// genuinely required by a diagnostic; prefer <see cref="Describe"/>.
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