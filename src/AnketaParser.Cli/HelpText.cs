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
              source    Сбор Двача и диагностика отбора; список операций: source help.

            Параметры:
              --json         Машинный отчёт doctor/version: один JSON-документ в stdout,
                             диагностика и логи в stderr. Справка остаётся текстовой.
              -h, --help     Показать эту справку.
              -v, --version  Показать версию инструмента.

            Коды завершения:
              0  команда выполнена успешно; это само по себе не означает, что система здорова
              1  непредвиденная внутренняя ошибка
              2  неверные аргументы командной строки
              3  некорректная конфигурация
              4  настроенная зависимость недоступна

            Для оценки готовности смотрите результат doctor. Код 0 команды source status
            подтверждает успешный запрос к хранилищу, а не наличие сохранённого опроса:
            в новой базе статус равен never_scanned. Наличие и результат опроса проверяйте
            по полям status и lastSuccessAt.

            Конфигурация:
              appsettings.json, appsettings.<DOTNET_ENVIRONMENT>.json и appsettings.Local.json
              рядом с программой, затем переменные окружения с префиксом ANKETAPARSER_
              (вложенные ключи разделяются двойным подчёркиванием, например
              ANKETAPARSER_Database__ConnectionString).
            """;
    }
}