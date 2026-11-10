using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>Validates <see cref="DiagnosticsSettings"/>.</summary>
public sealed class DiagnosticsSettingsValidator : IValidateOptions<DiagnosticsSettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DiagnosticsSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        HostSettingsValidator.RequireRange(failures, "Diagnostics:ProbeTimeoutSeconds", options.ProbeTimeoutSeconds, 1, 120);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}