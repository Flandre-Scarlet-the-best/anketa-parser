# Конфигурация

[Все руководства](README.md) · [Диагностика](diagnostics.md)

Настройки хоста и CLI загружаются одним
[конвейером](../src/AnketaParser.Infrastructure/Configuration/AnketaParserConfiguration.cs).
Чтобы изменить значение, сначала определите каталог приложения и источник с
наибольшим приоритетом.

## Порядок источников

От низкого приоритета к высокому:

1. Необязательный `appsettings.json` в каталоге приложения (`AppContext.BaseDirectory`).
2. Необязательный `appsettings.<DOTNET_ENVIRONMENT>.json` там же.
3. Необязательный `appsettings.Local.json` там же; файл исключён из Git.
4. Переменные окружения `ANKETAPARSER_`; `__` означает вложенный ключ.

Например, `ANKETAPARSER_Database__Required` переопределяет `Database:Required`.
`DOTNET_ENVIRONMENT` без префикса выбирает окружение; пустое значение означает
`Production`, пробелы по краям убираются. Отсутствие каталога или JSON-файлов
допустимо: остаются значения классов настроек и переменные окружения. Файлы
не перечитываются автоматически (`reloadOnChange: false`), после изменения
нужен новый запуск. Аргументы Host не являются источником конфигурации.

При `dotnet run` каталог приложения находится в `bin/<конфигурация>/net10.0/`
соответствующего проекта, а не в корне репозитория. В образе это `/app/host`
и `/app/cli`. Локальный JSON, положенный только в корень checkout, не применяется.
Для разового запуска удобнее переменные окружения.

## Ключи и значения

Значения и диапазоны задают [классы и валидаторы](../src/AnketaParser.Infrastructure/Configuration/).
В таблице показаны значения классов; отличия поставляемых JSON указаны ниже.

| Ключ | Тип | По умолчанию | Ограничение и назначение |
| --- | --- | --- | --- |
| `Host:InstanceName` | string | `anketa-parser` | Непустое, не только пробелы, до 100 символов; имя в отчётах |
| `Host:ShutdownTimeoutSeconds` | int | `20` | 1–600; ожидание штатной остановки |
| `Host:StartupDiagnosticsEnabled` | bool | `true` | Начальная проверка готовности |
| `Host:ReadinessCheckIntervalSeconds` | int | `60` | 0–86400; `0` отключает монитор |
| `Database:ConnectionString` | string | пусто | Строка Npgsql; секрет; пусто означает отсутствие настройки |
| `Database:Required` | bool | `false` | Отказ начальной проверки останавливает хост при `true` |
| `Database:ConnectTimeoutSeconds` | int | `5` | 1–120; подключение |
| `Database:CommandTimeoutSeconds` | int | `5` | 1–600; команда проверки |
| `Diagnostics:ProbeTimeoutSeconds` | int | `5` | 1–120; ограничение одной пробы агрегатором |
| `Logging:LogLevel:*` | string | Зависит от проекта | Уровни .NET по категориям |
| `Logging:Console:FormatterName` | string | `simple` в JSON | Формат консоли Host: `simple` или `json` |

В [Host appsettings.json](../src/AnketaParser.Host/appsettings.json) уровень по
умолчанию `Information`, категория `Microsoft.Extensions.Hosting` — `Warning`.
[Development](../src/AnketaParser.Host/appsettings.Development.json) повышает
детализацию до `Debug` и меняет интервал на 30 секунд. В
[CLI appsettings.json](../src/AnketaParser.Cli/appsettings.json) уровень `Warning`
и интервал `0`. CLI самостоятельно включает простой консольный форматтер и
фильтрует логи ниже `Warning`; настройка формата Host на него не переносится.
Compose задаёт Host формат `json` и обязательную БД через окружение.

Непустая строка подключения должна разбираться Npgsql и содержать `Host`,
`Database`, а также хотя бы одно из `Username`/`Password`, как требует текущий
валидатор. `Required=true` с пустой строкой недопустимо. Для пробы Npgsql
переопределяются таймауты, `ApplicationName=anketa-parser` и `Pooling=false`.

## Безопасное локальное переопределение

Пример без секретов для PowerShell; `finally` восстанавливает прежнее значение:

```powershell
$previousName = $env:ANKETAPARSER_Host__InstanceName
try {
    $env:ANKETAPARSER_Host__InstanceName = 'local-check'
    dotnet run --project src/AnketaParser.Cli -- doctor --json
} finally {
    $env:ANKETAPARSER_Host__InstanceName = $previousName
}
```

Эквивалент для Bash/WSL, только на время команды:

```bash
ANKETAPARSER_Host__InstanceName=local-check dotnet run --project src/AnketaParser.Cli -- doctor --json
```

Ожидайте `runtime.instanceName = "local-check"`. Для постоянных локальных
параметров можно создать в каталоге приложения игнорируемый `appsettings.Local.json`:

```json
{
  "Host": { "InstanceName": "local-check" },
  "Diagnostics": { "ProbeTimeoutSeconds": 10 }
}
```

Не добавляйте секреты в отслеживаемые JSON. `.env` читается Docker Compose,
а сам .NET-конвейер его не читает. Переменные Compose и запуск контейнеров
описаны в [эксплуатации](operations.md).

Если значение не меняется, проверьте каталог JSON, выбранное окружение и более
приоритетную переменную. `configuration.sources` показывает участвовавшие
файлы и источник окружения, но не сырые значения. Невалидные типизированные
настройки дают код `3`. Повреждённый синтаксис JSON у Host обрабатывается как
внутренняя ошибка `1`, а у CLI загрузка выполняется до обработчика исключений:
возможен необработанный отказ без JSON и без нормализованного кода приложения.

## Настройки хранилища

Секции Backup, Provision и Restore используют тот же порядок загрузки
конфигурации. Их параметры и
права описаны в [регламенте хранения](persistence.md). Host не использует
секреты Provision/Restore; Compose передаёт их только обслуживающим операциям.
