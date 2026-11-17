using AnketaParser.Infrastructure.Persistence;

using Microsoft.Extensions.Options;

using Npgsql;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Проверяет настройки резервирования в общем конвейере Host и CLI.</summary>
public sealed class BackupSettingsValidator : IValidateOptions<BackupSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BackupSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.IntervalSeconds < 1)
            failures.Add("Backup:IntervalSeconds должен быть не меньше 1.");
        if (options.TimeoutSeconds < 1)
            failures.Add("Backup:TimeoutSeconds должен быть не меньше 1.");
        if (options.Daily < 1)
            failures.Add("Backup:Daily должен быть не меньше 1.");
        if (options.Weekly < 0)
            failures.Add("Backup:Weekly не должен быть отрицательным.");
        if (options.Monthly < 0)
            failures.Add("Backup:Monthly не должен быть отрицательным.");
        if (options.MaxBytes < 1)
            failures.Add("Backup:MaxBytes должен быть не меньше 1.");
        if (options.RestoreJobs is < 1 or > 16)
            failures.Add("Backup:RestoreJobs должен быть в диапазоне от 1 до 16.");

        if (!ConnectionStringPassword.TryApply(options.ConnectionString, options.Password, out var effectiveConnectionString))
        {
            failures.Add("Backup:ConnectionString не является корректной строкой подключения PostgreSQL.");
        }
        else
        {
            options.ConnectionString = effectiveConnectionString;
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                try
                {
                    PostgresToolConnection.Validate(new NpgsqlConnectionStringBuilder(options.ConnectionString));
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException)
                {
                    failures.Add("Backup:ConnectionString содержит параметры, не поддерживаемые утилитами PostgreSQL.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}