using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

internal static class ConnectionStringPassword
{
    internal static bool TryApply(string connectionString, string? password, out string effectiveConnectionString)
    {
        effectiveConnectionString = connectionString;
        if (password is null)
            return true;

        try
        {
            effectiveConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Password = password,
            }.ConnectionString;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }
}