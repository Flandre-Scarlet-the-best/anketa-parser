using System.Globalization;

using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Проверяет <see cref="HostSettings"/> при старте и в диагностике CLI.</summary>
public sealed class HostSettingsValidator : IValidateOptions<HostSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HostSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            failures.Add("Host:InstanceName не должен быть пустым.");
        }
        else if (options.InstanceName.Length > 100)
        {
            failures.Add("Host:InstanceName не должен превышать 100 символов.");
        }

        RequireRange(failures, "Host:ShutdownTimeoutSeconds", options.ShutdownTimeoutSeconds, 1, 600);
        RequireRange(failures, "Host:ReadinessCheckIntervalSeconds", options.ReadinessCheckIntervalSeconds, 0, 86_400);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static void RequireRange(List<string> failures, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{key} должен быть в диапазоне от {min} до {max}."));
        }
    }
}