using System.Globalization;

using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Validates <see cref="HostSettings"/> at startup and in the CLI diagnostics path.</summary>
public sealed class HostSettingsValidator : IValidateOptions<HostSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HostSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            failures.Add("Host:InstanceName must not be empty.");
        }
        else if (options.InstanceName.Length > 100)
        {
            failures.Add("Host:InstanceName must be 100 characters or shorter.");
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
                $"{key} must be between {min} and {max} but was {value}."));
        }
    }
}