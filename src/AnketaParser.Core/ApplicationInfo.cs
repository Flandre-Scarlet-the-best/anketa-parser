using System.Reflection;

namespace AnketaParser.Core;

/// <summary>
/// Descriptive identity of an executable: product name and build version.
/// </summary>
/// <param name="ProductName">Stable product name, for example <c>anketa-parser</c>.</param>
/// <param name="Version">Version string suitable for humans and machine-readable diagnostics.</param>
public sealed record ApplicationInfo(string ProductName, string Version)
{
    /// <summary>Product name used by both entry points and by diagnostics documents.</summary>
    public const string DefaultProductName = "anketa-parser";

    /// <summary>
    /// Reads the informational version of the given assembly, falling back to the assembly version.
    /// </summary>
    public static ApplicationInfo FromAssembly(Assembly assembly, string productName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational.Split('+', 2)[0];

        return new ApplicationInfo(productName, version);
    }

    /// <summary>Reads the identity of the currently executing entry assembly.</summary>
    public static ApplicationInfo ForEntryAssembly(string productName = DefaultProductName)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ApplicationInfo).Assembly;
        return FromAssembly(assembly, productName);
    }
}