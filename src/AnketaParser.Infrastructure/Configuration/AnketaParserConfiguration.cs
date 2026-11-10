using AnketaParser.Core.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Builds the single configuration pipeline shared by the long-running host and the CLI:
/// JSON files from the content root, then environment variables with the
/// <c>ANKETAPARSER_</c> prefix (nested keys use the standard <c>__</c> separator).
/// </summary>
public static class AnketaParserConfiguration
{
    /// <summary>Prefix that marks environment variables owned by this application.</summary>
    public const string EnvironmentVariablePrefix = "ANKETAPARSER_";

    /// <summary>Environment variable that selects the hosting environment.</summary>
    public const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";

    /// <summary>Default hosting environment.</summary>
    public const string DefaultEnvironmentName = "Production";

    /// <summary>Optional machine-specific file, excluded from Git.</summary>
    public const string LocalSettingsFileName = "appsettings.Local.json";

    /// <summary>Resolves the hosting environment from <c>DOTNET_ENVIRONMENT</c>.</summary>
    public static string ResolveEnvironmentName()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentNameVariable);
        return string.IsNullOrWhiteSpace(value) ? DefaultEnvironmentName : value.Trim();
    }

    /// <summary>
    /// Applies the configuration pipeline to <paramref name="builder"/> and returns the sources
    /// that actually contributed, for diagnostics output.
    /// </summary>
    public static IReadOnlyList<ConfigurationSourceInfo> Apply(
        IConfigurationBuilder builder,
        string contentRootPath,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var sources = new List<ConfigurationSourceInfo>(3);

        // A missing content root must not break startup: the process falls back to defaults and
        // environment variables instead of failing inside the file provider.
        if (Directory.Exists(contentRootPath))
        {
            builder.SetBasePath(contentRootPath);

            string[] files =
            [
                "appsettings.json",
                $"appsettings.{environmentName}.json",
                LocalSettingsFileName,
            ];

            foreach (var file in files)
            {
                builder.AddJsonFile(file, optional: true, reloadOnChange: false);
                if (File.Exists(Path.Combine(contentRootPath, file)))
                {
                    sources.Add(new ConfigurationSourceInfo(file, "file"));
                }
            }
        }

        builder.AddEnvironmentVariables(EnvironmentVariablePrefix);
        sources.Add(new ConfigurationSourceInfo(
            $"environment variables ({EnvironmentVariablePrefix}*)",
            "environment"));

        return sources;
    }
}