using System.Reflection;

namespace AnketaParser.Core;

/// <summary>Название продукта и версия сборки исполняемого приложения.</summary>
/// <param name="ProductName">Стабильное название продукта, например <c>anketa-parser</c>.</param>
/// <param name="Version">Версия для человека и машинной диагностики.</param>
public sealed record ApplicationInfo(string ProductName, string Version)
{
    /// <summary>Название продукта для обеих точек входа и диагностических документов.</summary>
    public const string DefaultProductName = "anketa-parser";

    /// <summary>Читает информационную версию сборки, а при её отсутствии — версию сборки.</summary>
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

    /// <summary>Читает сведения о текущей входной сборке.</summary>
    public static ApplicationInfo ForEntryAssembly(string productName = DefaultProductName)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ApplicationInfo).Assembly;
        return FromAssembly(assembly, productName);
    }
}