using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Validates <see cref="DatabaseSettings"/>. The validator never echoes the connection string,
/// so validation messages are safe for logs and machine-readable CLI output.
/// </summary>
public sealed class DatabaseSettingsValidator : IValidateOptions<DatabaseSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DatabaseSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        HostSettingsValidator.RequireRange(failures, "Database:ConnectTimeoutSeconds", options.ConnectTimeoutSeconds, 1, 120);
        HostSettingsValidator.RequireRange(failures, "Database:CommandTimeoutSeconds", options.CommandTimeoutSeconds, 1, 600);

        if (options.Required && !options.IsConfigured)
        {
            failures.Add(
                "Database:Required is true but Database:ConnectionString is empty. " +
                "Set ANKETAPARSER_Database__ConnectionString or disable Database:Required.");
        }

        if (options.IsConfigured && !TryParseConnectionString(options.ConnectionString, out var error))
        {
            failures.Add($"Database:ConnectionString is not a valid PostgreSQL connection string ({error}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool TryParseConnectionString(string connectionString, out string error)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host))
            {
                error = "Host is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                error = "Database is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(builder.Username) && string.IsNullOrWhiteSpace(builder.Password))
            {
                error = "Username is missing";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            error = exception.GetType().Name;
            return false;
        }
    }
}