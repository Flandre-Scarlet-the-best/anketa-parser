using AnketaParser.Core.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Configuration;

/// <summary>
/// Единый конвейер конфигурации Host и CLI: JSON из каталога настроек, затем переменные окружения с
/// префиксом <c>ANKETAPARSER_</c> и разделителем <c>__</c>.
/// </summary>
public static class AnketaParserConfiguration
{
    /// <summary>Префикс переменных окружения приложения.</summary>
    public const string EnvironmentVariablePrefix = "ANKETAPARSER_";

    /// <summary>Переменная окружения для выбора среды хостинга.</summary>
    public const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";

    /// <summary>Среда хостинга по умолчанию.</summary>
    public const string DefaultEnvironmentName = "Production";

    /// <summary>Необязательный локальный файл, исключённый из Git.</summary>
    public const string LocalSettingsFileName = "appsettings.Local.json";

    /// <summary>
    /// Выполняет построение, которое читает JSON-конфигурацию, и безопасно классифицирует ошибки
    /// открытия файла.
    /// </summary>
    /// <typeparam name="T">Тип результата операции.</typeparam>
    /// <param name="build">Операция построения конфигурации.</param>
    /// <returns>Результат операции.</returns>
    public static T ExecuteWithConfigurationFileReadErrors<T>(Func<T> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        try
        {
            return build();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateJsonFileReadFailureException();
        }
    }

    /// <summary>Определяет среду хостинга по <c>DOTNET_ENVIRONMENT</c>.</summary>
    public static string ResolveEnvironmentName()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentNameVariable);
        return string.IsNullOrWhiteSpace(value) ? DefaultEnvironmentName : value.Trim();
    }

    /// <summary>
    /// Применяет конвейер к <paramref name="builder"/> и возвращает использованные источники для
    /// диагностического отчёта.
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

        // Без каталога настроек используются значения по умолчанию и переменные окружения.
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
                builder.AddJsonFile(source =>
                {
                    source.Path = file;
                    source.Optional = true;
                    source.ReloadOnChange = false;
                    source.OnLoadException = context =>
                    {
                        if (context.Exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
                        {
                            throw CreateJsonFileReadFailureException();
                        }
                    };
                });
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

    private static OptionsValidationException CreateJsonFileReadFailureException() => new(
        Options.DefaultName,
        typeof(AnketaParserConfiguration),
        ["Не удалось прочитать файл настроек JSON: проверьте синтаксис и права доступа."]);
}