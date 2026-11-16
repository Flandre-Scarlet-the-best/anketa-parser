using AnketaParser.Core;
using AnketaParser.Core.Diagnostics;
using AnketaParser.Infrastructure;
using AnketaParser.Infrastructure.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnketaParser.Host;

/// <summary>
/// Композиция долгоживущего хоста: конфигурация, логирование, типизированные настройки и службы
/// жизненного цикла. Библиотеки регистрируют адаптеры, а хост собирает граф зависимостей.
/// </summary>
public static class HostApplication
{
    /// <summary>Точка входа хоста, отделённая от <c>Program</c> для проверки в тестах.</summary>
    /// <param name="args">Аргументы процесса, не используемые как конфигурация.</param>
    /// <param name="cancellationToken">Запрашивает штатную остановку хоста.</param>
    /// <param name="contentRootPath">Каталог настроек, переопределяемый в тестах.</param>
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        string? contentRootPath = null)
    {
        try
        {
            using var host = CreateHost(args, contentRootPath);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OptionsValidationException exception)
        {
            WriteConfigurationErrors(exception);
            return ExitCodes.InvalidConfiguration;
        }
        catch (DependencyUnavailableException)
        {
            Console.Error.WriteLine("Обязательная зависимость недоступна.");
            return ExitCodes.DependencyUnavailable;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Внутренняя ошибка: {exception.GetType().Name}.");
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>Собирает хост без запуска для тестов и проверки композиции.</summary>
    /// <param name="args">Аргументы процесса, намеренно исключённые из конфигурации.</param>
    /// <param name="contentRootPath">Каталог настроек; по умолчанию — каталог приложения.</param>
    /// <returns>Собранный и настроенный экземпляр хоста.</returns>
    public static IHost CreateHost(string[] args, string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var contentRoot = contentRootPath ?? AppContext.BaseDirectory;
        var environmentName = AnketaParserConfiguration.ResolveEnvironmentName();

        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            // Настройки читает только общий конвейер с безопасной классификацией ошибок.
            DisableDefaults = true,
            ContentRootPath = contentRoot,
            EnvironmentName = environmentName,
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = builder.Environment.IsDevelopment(),
            ValidateOnBuild = builder.Environment.IsDevelopment(),
        }));

        builder.Configuration.Sources.Clear();
        var configurationSources = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(
            () => AnketaParserConfiguration.Apply(builder.Configuration, contentRoot, environmentName));

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddConsole();

        builder.Services.AddAnketaParserInfrastructure(
            builder.Configuration,
            environmentName,
            configurationSources);

        builder.Services.AddOptions<HostOptions>().Configure<IOptions<HostSettings>>((options, settings) =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(settings.Value.ShutdownTimeoutSeconds);
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        builder.Services.AddHostedService<StartupDiagnosticsService>();
        builder.Services.AddHostedService<ReadinessMonitorService>();

        var host = AnketaParserConfiguration.ExecuteWithConfigurationFileReadErrors(builder.Build);
        try
        {
            // Проверяем настройки до запуска Generic Host, который логирует исключения старта целиком.
            _ = host.Services.GetRequiredService<IOptions<HostSettings>>().Value;
            _ = host.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            _ = host.Services.GetRequiredService<IOptions<DiagnosticsSettings>>().Value;
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static void WriteConfigurationErrors(OptionsValidationException exception)
    {
        Console.Error.WriteLine("Конфигурация некорректна. Исправьте настройки перед запуском:");
        foreach (var failure in exception.Failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }
    }
}