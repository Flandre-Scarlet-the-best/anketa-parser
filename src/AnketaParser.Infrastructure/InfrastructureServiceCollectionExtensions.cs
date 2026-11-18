using AnketaParser.Core.Diagnostics;
using AnketaParser.Core.Persistence;
using AnketaParser.Infrastructure.Configuration;
using AnketaParser.Infrastructure.Diagnostics;
using AnketaParser.Infrastructure.Persistence;
using AnketaParser.Infrastructure.Sources;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure;

/// <summary>
/// Регистрирует инфраструктурные адаптеры. Исполняемое приложение определяет используемые возможности и
/// службы жизненного цикла.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет типизированные настройки с валидацией, проверки готовности, агрегатор диагностики и
    /// поставщик сведений о среде выполнения.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Итоговая конфигурация.</param>
    /// <param name="environmentName">Среда хостинга.</param>
    /// <param name="configurationSources">Источники для диагностического отчёта.</param>
    public static IServiceCollection AddAnketaParserInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName,
        IReadOnlyList<ConfigurationSourceInfo> configurationSources)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentNullException.ThrowIfNull(configurationSources);

        services.AddOptions<HostSettings>()
            .Configure(options => BindSettings(configuration.GetSection(HostSettings.SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostSettings>, HostSettingsValidator>();
        services.AddSingleton<IOptionsChangeTokenSource<HostSettings>>(
            new ConfigurationChangeTokenSource<HostSettings>(
                Options.DefaultName, configuration.GetSection(HostSettings.SectionName)));

        services.AddOptions<DatabaseSettings>()
            .Configure(options => BindSettings(configuration.GetSection(DatabaseSettings.SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseSettings>, DatabaseSettingsValidator>();
        services.AddSingleton<IOptionsChangeTokenSource<DatabaseSettings>>(
            new ConfigurationChangeTokenSource<DatabaseSettings>(
                Options.DefaultName, configuration.GetSection(DatabaseSettings.SectionName)));

        services.AddOptions<DiagnosticsSettings>()
            .Configure(options => BindSettings(configuration.GetSection(DiagnosticsSettings.SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DiagnosticsSettings>, DiagnosticsSettingsValidator>();
        services.AddSingleton<IOptionsChangeTokenSource<DiagnosticsSettings>>(
            new ConfigurationChangeTokenSource<DiagnosticsSettings>(
                Options.DefaultName, configuration.GetSection(DiagnosticsSettings.SectionName)));

        services.AddOptions<BackupSettings>()
            .Configure(options => BindSettings(configuration.GetSection(BackupSettings.SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackupSettings>, BackupSettingsValidator>();
        services.AddSingleton<IOptionsChangeTokenSource<BackupSettings>>(
            new ConfigurationChangeTokenSource<BackupSettings>(
                Options.DefaultName, configuration.GetSection(BackupSettings.SectionName)));

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<DvachSettings>()
            .Configure(options => BindSettings(configuration.GetSection(DvachSettings.SectionName), options));
        services.AddSingleton<IValidateOptions<DvachSettings>, DvachSettingsValidator>();
        services.TryAddSingleton<HttpClient>(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.TryAddSingleton(provider => new DvachClient(
            provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new DvachIngestion(
            provider.GetRequiredService<IOptions<DvachSettings>>(),
            provider.GetRequiredService<IOptions<DatabaseSettings>>(),
            provider.GetRequiredService<DvachClient>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddDbContext<ProductDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IOptions<DatabaseSettings>>().Value.ConnectionString,
                ProductDbContext.ConfigureProvider));
        services.AddScoped<IProductStore, ProductStore>();
        services.AddSingleton(new EnvironmentDescriptor(environmentName));
        services.AddSingleton(new ConfigurationSourceSet(configurationSources));
        services.TryAddSingleton<ReadinessStateStore>();

        services.TryAddSingleton<IRuntimeSnapshotProvider, RuntimeSnapshotProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessProbe, PostgresReadinessProbe>());
        if (!string.IsNullOrWhiteSpace(configuration[$"{BackupSettings.SectionName}:Directory"]))
            services.AddSingleton<IReadinessProbe>(provider =>
                new BackupReadinessProbe(provider.GetRequiredService<IOptions<BackupSettings>>()));
        services.TryAddSingleton<DiagnosticsRunner>();

        return services;
    }

    /// <summary>
    /// Разрешает типизированные настройки в том же составе, что и при старте Host:
    /// обращение к значению запускает зарегистрированные валидаторы.
    /// </summary>
    /// <param name="provider">Поставщик служб с зарегистрированной инфраструктурой.</param>
    /// <exception cref="OptionsValidationException">Конфигурация не прошла проверку.</exception>
    public static void ValidateSettings(this IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _ = provider.GetRequiredService<IOptions<HostSettings>>().Value;
        _ = provider.GetRequiredService<IOptions<DatabaseSettings>>().Value;
        _ = provider.GetRequiredService<IOptions<DiagnosticsSettings>>().Value;
        _ = provider.GetRequiredService<IOptions<BackupSettings>>().Value;
        _ = provider.GetRequiredService<IOptions<DvachSettings>>().Value;
    }

    private static void BindSettings<T>(IConfigurationSection section, T options)
        where T : class
    {
        try
        {
            section.Bind(options);
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            // Биндер включает исходное значение в исключение; наружу выходит только имя секции.
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(T),
                [$"Секция {section.Key}: значение настройки не соответствует ожидаемому типу."]);
        }
    }
}