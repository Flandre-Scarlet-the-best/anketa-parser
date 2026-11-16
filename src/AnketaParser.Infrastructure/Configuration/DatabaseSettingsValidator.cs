using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Проверяет <see cref="DatabaseSettings"/>, не повторяя секретную строку подключения в сообщениях.</summary>
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
                "Database:Required равен true, но Database:ConnectionString пуст. " +
                "Задайте ANKETAPARSER_Database__ConnectionString или отключите Database:Required.");
        }

        if (options.IsConfigured && !TryParseConnectionString(options.ConnectionString, out var error))
        {
            failures.Add($"Database:ConnectionString не является корректной строкой подключения PostgreSQL ({error}).");
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
                error = "не указан Host";
                return false;
            }

            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                error = "не указан Database";
                return false;
            }

            if (string.IsNullOrWhiteSpace(builder.Username) && string.IsNullOrWhiteSpace(builder.Password))
            {
                error = "не указан Username";
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