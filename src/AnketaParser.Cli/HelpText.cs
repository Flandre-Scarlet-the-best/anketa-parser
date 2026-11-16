namespace AnketaParser.Cli;

/// <summary>Справка по командам и кодам завершения.</summary>
internal static class HelpText
{
    internal const string LaunchCommand = "dotnet AnketaParser.Cli.dll";

    public static string Usage(string productName, string version)
    {
        return $"""
            {productName} CLI {version}

            Использование (из каталога CLI):
              {LaunchCommand} <команда> [--json]

            Команды:
              doctor    Проверить конфигурацию, среду выполнения и готовность зависимостей.
              version   Показать версию инструмента.
              help      Показать эту справку.
              storage   Обслуживание схемы и резервных копий; список операций: storage help.

            Параметры:
              --json         Машинный отчёт doctor/version: один JSON-документ в stdout,
                             диагностика и логи в stderr. Справка остаётся текстовой.
              -h, --help     Показать эту справку.
              -v, --version  Показать версию инструмента.

            Коды завершения:
              0  успех: конфигурация верна, зависимости доступны или не настроены
              1  непредвиденная внутренняя ошибка
              2  неверные аргументы командной строки
              3  некорректная конфигурация
              4  настроенная зависимость недоступна

            Конфигурация:
              appsettings.json, appsettings.<DOTNET_ENVIRONMENT>.json and appsettings.Local.json
              рядом с программой, затем переменные окружения с префиксом ANKETAPARSER_
              (вложенные ключи разделяются двойным подчёркиванием, например
              ANKETAPARSER_Database__ConnectionString).
            """;
    }
}