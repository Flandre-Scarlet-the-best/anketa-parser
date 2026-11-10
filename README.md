# anketa-parser

Инженерная основа сервиса `anketa-parser` на C#/.NET 10: единое .NET-решение, долгоживущий хост,
минимальный CLI для агента/администратора, единая конфигурация, диагностируемый жизненный цикл и
контейнерное окружение PostgreSQL.

## Текущий статус

Это **инфраструктурный этап**. В репозитории есть работающая основа, а не готовый продукт:

- работает долгоживущий хост на Generic Host: конфигурация, валидация, структурированное логирование,
  самодиагностика при старте, периодический монитор готовности, корректное завершение по SIGTERM/Ctrl+C;
- работает CLI: справка, версия и диагностика (`doctor`) с машиночитаемым режимом `--json`;
- работает контейнерная инфраструктура: Docker Compose поднимает хост и PostgreSQL 18, проверяет их
  состояние, сохраняет данные БД и ничего не публикует наружу;
- есть unit-тесты инфраструктурных контрактов и отдельный включаемый интеграционный smoke-тест Docker/PostgreSQL.

Чего на этом этапе **нет** (и это не выдаётся за готовую функциональность):

- парсинга, работы с анкетами, контактами, аккаунтами, чёрными списками, жалобами, подписками;
- Telegram Bot API / MTProto, LLM-провайдеров, поиска уведомлений;
- доменной схемы PostgreSQL, EF Core-миграций, pgvector, Redis, RabbitMQ, очередей заданий;
- административного HTTP API, MCP, Agent Skills и прочей отложенной автоматизации.

PostgreSQL на этом этапе — запускаемая и проверяемая инфраструктура: единственная зависимость,
которую продукт проверяет, это доступность сервера БД (`SELECT 1`). Таблиц анкет, репозиториев и
фейковых доменных моделей здесь намеренно нет.

## Архитектура и границы

| Проект | Роль | Зависимости |
| --- | --- | --- |
| `src/AnketaParser.Core` | Контракты и прикладная логика диагностики: коды завершения, контракт проверки готовности, агрегатор отчёта, модели отчёта. | Нет пакетов NuGet. |
| `src/AnketaParser.Infrastructure` | Адаптеры: загрузка конфигурации, типизированные настройки и их валидация, проверка PostgreSQL через Npgsql, снимок окружения, хранилище состояния готовности. | `AnketaParser.Core`, Npgsql, абстракции конфигурации/опций/логирования/хостинга. |
| `src/AnketaParser.Host` | **Композиция зависимостей** и жизненный цикл долгоживущего процесса: хост, логирование, self-check при старте, монитор готовности, коды выхода. | `Core`, `Infrastructure`. |
| `src/AnketaParser.Cli` | Композиция и точка входа CLI: `help`, `version`, `doctor` (в том числе `--json`). | `Core`, `Infrastructure`. |
| `tests/AnketaParser.Tests` | Тесты инфраструктурных контрактов: конфигурация, коды выхода, JSON-контракт CLI, сборка DI-графа, остановка по отмене, probe-агрегация. Работают без Docker и без внешних сервисов. | Все проекты `src/`. |
| `tests/AnketaParser.DockerTests` | Интеграционный smoke Docker/PostgreSQL, по умолчанию **пропускается**. | `xunit.v3`, `docker` CLI. |

Направление зависимостей: `Host`/`Cli` → `Infrastructure` → `Core`. Циклов нет, `Core` не знает
ни про Telegram, ни про HTTP, ни про PostgreSQL, ни про CLI.

Соблюдаемые инварианты:

- точка композиции принадлежит исполняемым проектам; `Infrastructure` только регистрирует свои
  адаптеры расширением `AddAnketaParserInfrastructure`;
- прикладная логика диагностики одна: и хост, и CLI используют `DiagnosticsRunner` и те же пробы,
  дублирования проверок нет;
- инфраструктурные адаптеры не содержат доменных правил (доменных правил на этом этапе ещё нет);
- архитектура не требует нескольких микросервисов: это один процесс + одна БД.

## Требования окружения

- **.NET SDK 10.0.402** (LTS). Версия зафиксирована в `global.json` с `rollForward: latestFeature`,
  поэтому подойдёт любой SDK 10.0.x не ниже 10.0.402.
- **Docker Engine / Docker Desktop** с Compose v2 — только для контейнерного запуска и
  интеграционного smoke-теста. Unit-тесты Docker не требуют.
- Поддерживаемые платформы: Windows 10/11 (нативно или через WSL2), Linux. Все команды одинаковы.

## Быстрый старт локально

```bash
dotnet restore --locked-mode
dotnet build
dotnet test
```

Запуск хоста (долгоживущий процесс; без внешних сервисов работает в режиме инфраструктурной
диагностики):

```bash
dotnet run --project src/AnketaParser.Host
```

Остановка — `Ctrl+C` (или SIGTERM в WSL/Linux); хост завершится штатно с кодом 0.

Диагностика из CLI:

```bash
dotnet run --project src/AnketaParser.Cli -- --version
dotnet run --project src/AnketaParser.Cli -- --help
dotnet run --project src/AnketaParser.Cli -- doctor            # человекочитаемый отчёт
dotnet run --project src/AnketaParser.Cli -- doctor --json     # один JSON-документ в stdout
```

## Запуск в Docker

```bash
# 1. Подготовить локальные значения (файл .env исключён из Git)
cp .env.example .env        # Windows PowerShell: Copy-Item .env.example .env
# отредактируйте POSTGRES_PASSWORD в .env

# 2. Собрать и поднять хост + PostgreSQL
docker compose up -d --build

# 3. Состояние и логи
docker compose ps
docker compose logs -f host

# 4. Обычное выключение (данные БД сохраняются)
docker compose down

# 5. Полная очистка, включая том с данными
docker compose down --volumes
```

Что делает этот файл `compose.yaml`:

- `postgres` — официальный образ `postgres:18.6-alpine`, постоянный том `postgres-data`
  (монтируется в `/var/lib/postgresql`, как ожидает образ 18.x), healthcheck `pg_isready`;
- `host` — multi-stage сборка из `Dockerfile`, стартует только после `service_healthy` у PostgreSQL,
  работает не от root (`APP_UID` официального образа), healthcheck — вызов CLI `doctor --json`
  **внутри** контейнера;
- наружу **не публикуется ни один порт**: ни PostgreSQL, ни какой-либо административный HTTP-интерфейс.

Локальная отладка БД через явно включаемый loopback-режим:

```bash
docker compose -f compose.yaml -f compose.debug.yaml up -d postgres
psql "host=127.0.0.1 port=55432 dbname=anketa_parser user=anketa_parser"
```

Порт публикуется только на `127.0.0.1` и только в этом дополнительном файле.

## Команды CLI и контракт машиночитаемого режима

```
anketa-parser-cli <command> [--json]
```

| Команда | Назначение |
| --- | --- |
| `doctor` (по умолчанию) | Проверяет конфигурацию, окружение и готовность зависимостей. |
| `version` | Печатает версию инструмента. |
| `help` / `-h` / `--help` | Печатает справку. |

Флаг `--json` включает машиночитаемый режим: в `stdout` попадает **ровно один JSON-документ**,
а все логи и человекочитаемая диагностика уходят в `stderr`.

Коды завершения (стабильный контракт, см. `AnketaParser.Core.ExitCodes`):

| Код | Значение |
| --- | --- |
| `0` | Успех: конфигурация валидна, зависимости здоровы или не настроены. |
| `1` | Непредвиденная внутренняя ошибка. |
| `2` | Некорректные аргументы командной строки. |
| `3` | Конфигурация отсутствует или невалидна. |
| `4` | Недоступна обязательная зависимость. |

Пример `doctor --json` (сокращён):

```json
{
  "schemaVersion": 1,
  "tool": "anketa-parser",
  "toolVersion": "0.1.0",
  "command": "doctor",
  "generatedAtUtc": "2026-01-01T00:00:00.0000000+00:00",
  "status": "healthy",
  "exitCode": 0,
  "runtime": {
    "framework": ".NET 10.0.14",
    "os": "Ubuntu 24.04.5 LTS",
    "architecture": "X64",
    "isContainer": true,
    "machineName": "container-host",
    "processId": 44,
    "uptimeSeconds": 0.37,
    "instanceName": "anketa-parser",
    "environment": "Production"
  },
  "configuration": {
    "valid": true,
    "sources": [
      { "name": "appsettings.json", "kind": "file" },
      { "name": "environment variables (ANKETAPARSER_*)", "kind": "environment" }
    ],
    "database": {
      "configured": true,
      "required": true,
      "target": "postgres:5432/anketa_parser",
      "connectTimeoutSeconds": 5
    }
  },
  "probes": [
    {
      "name": "postgres",
      "status": "healthy",
      "detail": "PostgreSQL 18.6 answered at postgres:5432/anketa_parser.",
      "durationMs": 143.08
    }
  ],
  "errors": []
}
```

Значения `status`: `healthy`, `degraded` (зависимость не настроена, готовность не заявляется),
`unhealthy` (настроенная зависимость не отвечает), `invalid_configuration`, `invalid_usage`.
Значения `probes[].status`: `healthy`, `skipped`, `unhealthy`. Секретов в документе нет:
строка подключения описывается только как `host:port/database`.

## Конфигурация

Источники применяются по возрастанию приоритета, одинаково для хоста и CLI:

1. `appsettings.json` в content root (для хоста — каталог сборки, в контейнере `/app/host`);
2. `appsettings.<DOTNET_ENVIRONMENT>.json` (например, `appsettings.Development.json` → `Debug`);
3. `appsettings.Local.json` — личные локальные переопределения, **исключён из Git**;
4. переменные окружения с префиксом `ANKETAPARSER_`, вложенные ключи через `__`.

Пример: `ANKETAPARSER_Database__ConnectionString`, `ANKETAPARSER_Host__ReadinessCheckIntervalSeconds`.

| Ключ | Тип | По умолчанию | Допустимо | Значение |
| --- | --- | --- | --- | --- |
| `Host:InstanceName` | string | `anketa-parser` | 1–100 символов | Имя экземпляра в логах и диагностике. |
| `Host:ShutdownTimeoutSeconds` | int | `20` | 1–600 | Сколько ждать штатного завершения. |
| `Host:StartupDiagnosticsEnabled` | bool | `true` | — | Выполнять self-check при старте. |
| `Host:ReadinessCheckIntervalSeconds` | int | `60` | 0–86400 | Интервал периодической проверки; `0` отключает монитор. |
| `Database:ConnectionString` | string | пусто | валидная строка Npgsql | Строка подключения. Пусто = интеграция не настроена. **Секрет.** |
| `Database:Required` | bool | `false` | — | `true`: недоступная БД останавливает хост, ложной готовности нет. |
| `Database:ConnectTimeoutSeconds` | int | `5` | 1–120 | Таймаут подключения. |
| `Database:CommandTimeoutSeconds` | int | `5` | 1–600 | Таймаут команды проверки. |
| `Diagnostics:ProbeTimeoutSeconds` | int | `5` | 1–120 | Предельное время одной проверки. |
| `Logging:LogLevel:*` | string | `Information` | уровни .NET | Уровни логирования по категориям. |
| `Logging:Console:FormatterName` | string | `simple` | `simple`, `json` | В контейнере включается `json`. |

Особенности:

- обязательных секретов на этом этапе нет: хост и CLI полностью работают без Telegram-токенов,
  ключей LLM и без PostgreSQL (тогда зависимость помечается `skipped`, статус `degraded`);
- неверные значения (пустое `InstanceName`, таймаут вне диапазона, `Database:Required=true` без
  строки подключения, неразбираемая строка подключения) дают явный отказ: хост завершается с кодом
  `3`, CLI — с кодом `3` и списком ошибок в `errors`;
- секреты не попадают ни в Git (`.env`, `appsettings.Local.json` исключены), ни в логи, ни в
  stdout CLI, ни в образ: `Dockerfile` копирует только исходники и `appsettings.json`;
- в `compose.yaml` есть локальный пароль-заглушка `local-development-password`. Это не секрет и не
  «боевой» токен: он нужен лишь для воспроизводимого `docker compose up` на машине владельца.
  Перед любым другим использованием задайте `POSTGRES_PASSWORD` в `.env`.

## Диагностика, логирование и готовность

- Логирование — `ILogger` с source-generated сообщениями (`[LoggerMessage]`), стабильными `EventId`
  и структурированными полями. Уровни настраиваются секцией `Logging`. В контейнере включён
  JSON-формат, второго файлового журнала нет — только `stdout`/`stderr`.
- Секреты в логи не попадают: строка подключения описывается как `host:port/database`, тексты
  ошибок Npgsql не пересказываются.
- **Liveness и readiness различаются**: живой процесс — это состояние контейнера; готовность
  зависимостей показывает отдельная проверка.
  - при старте выполняется self-check (`EventId 1002–1005`) и запоминается агрегатный статус;
  - при `Database:Required=true` и недоступной БД хост не «зеленеет», а завершается с кодом `4`;
  - периодический монитор (`EventId 3001–3006`) перепроверяет зависимости и пишет в лог только
    смену состояния; при незаданной БД он не крутит пустой цикл, а сообщает об этом и завершается;
  - Docker healthcheck вызывает `anketa-parser-cli doctor --json` внутри контейнера: код `4`
    переводит контейнер в `unhealthy`.

Полезные события: `1001` диагностика выключена, `1002` итог self-check, `1003` результат пробы,
`1004` обязательная зависимость недоступна, `1005` деградация без остановки, `2001` ошибка пробы
PostgreSQL, `3001–3006` жизненный цикл монитора готовности.

## Проверки

Локально и в CI выполняются одни и те же команды:

```bash
dotnet restore --locked-mode                 # восстановление строго по packages.lock.json
dotnet format anketa-parser.slnx --verify-no-changes
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
docker compose config --quiet                # валидность контейнерного описания
```

CI (`.github/workflows/ci.yml`) — базовый: restore в locked mode, проверка форматирования,
сборка, тесты и проверка `docker compose config` на официальном .NET SDK. Никаких сторонних
review-сервисов, публикации релизов и матриц раннеров.

Тесты по умолчанию (39 тестов):

- успешная и ошибочная загрузка конфигурации, приоритет источников, валидация опций;
- отсутствие секретов в описании строки подключения и в выводах;
- агрегация статусов диагностики, таймаут пробы, отсутствие утечки текста исключения, отмена;
- контракт CLI: `help`/`version`, коды выхода `0/2/3/4`, ровно один JSON-документ в `stdout`;
- сборка DI-графа хоста, self-check при старте, штатная остановка по отмене, обязательная
  и необязательная недоступная зависимость.

### Интеграционный smoke Docker/PostgreSQL

Сценарий автоматизирован отдельным тестовым проектом и по умолчанию пропускается (Docker может
отсутствовать). Включение:

```bash
# Linux/macOS/WSL
ANKETAPARSER_DOCKER_TESTS=1 dotnet test tests/AnketaParser.DockerTests

# Windows PowerShell
$env:ANKETAPARSER_DOCKER_TESTS = "1"; dotnet test tests/AnketaParser.DockerTests
```

Что проверяет тест:

1. `docker compose up --wait` поднимает PostgreSQL и хост, оба сервиса достигают `healthy`;
2. у контейнера PostgreSQL нет опубликованных портов (`docker port` пуст);
3. контейнер `host` действительно `healthy`, то есть readiness-проверка внутри контейнера реально
   соединилась с БД (образ собирается в `--locked-mode`);
4. в отдельной временной базе создаётся таблица-маркер и строка;
5. после `docker compose restart postgres` строка на месте — том сохраняет данные при обычном
   перезапуске контейнера;
6. после перезапуска БД контейнер `host` снова `healthy`;
7. стек удаляется (`down --volumes`), временный env-файл с паролем стирается.

Ручной эквивалент — команды из раздела «Запуск в Docker» плюс `psql` внутри контейнера:

```bash
docker compose exec -T postgres psql -U anketa_parser -d anketa_parser -tAc "SELECT count(*) FROM smoke_marker"
```

## Безопасное выключение

- `Ctrl+C` в терминале или `SIGTERM` (например, `docker stop`) инициируют штатную остановку:
  хост прекращает принимать работу, монитор готовности выходит из цикла, логируется
  `Periodic readiness monitoring stopped.` и процесс завершается с кодом `0`.
- Предельное время ожидания задаётся `Host:ShutdownTimeoutSeconds` (по умолчанию 20 с);
  `docker compose` даёт контейнеру `stop_grace_period: 30s`.
- Аварийных путей «убить процесс сразу» в конфигурации нет: при превышении таймаута хост
  завершается принудительно средствами хостинга, и это видно по логам.

## Структура репозитория

```
.
├── anketa-parser.slnx          # каноническая точка restore/build/test/format
├── global.json                 # версия SDK и раннер тестов (Microsoft.Testing.Platform)
├── Directory.Build.props       # общие настройки компиляции, nullable, анализаторы, lock-файлы
├── Directory.Packages.props    # централизованные версии NuGet
├── NuGet.config
├── Dockerfile
├── compose.yaml
├── compose.debug.yaml          # loopback-публикация PostgreSQL для отладки
├── .env.example
├── src/
│   ├── AnketaParser.Core/
│   ├── AnketaParser.Infrastructure/
│   ├── AnketaParser.Host/      # appsettings.json, appsettings.Development.json
│   └── AnketaParser.Cli/       # appsettings.json
└── tests/
    ├── AnketaParser.Tests/
    └── AnketaParser.DockerTests/
```

## Частые проблемы

| Симптом | Причина и решение |
| --- | --- |
| Хост завершился с кодом `3` | Невалидная конфигурация; список ошибок печатается в `stderr`. Исправьте значения `Host:*`, `Diagnostics:*`, `Database:*`. |
| Хост завершился с кодом `4` | `Database:Required=true`, но БД недоступна. Проверьте `docker compose ps`, строку подключения и `docker compose logs postgres`. |
| CLI `doctor --json` вернул `4` | Настроенная БД не отвечает; контейнер/сервис будет помечен `unhealthy`. |
| `docker compose up` ругается на `POSTGRES_PASSWORD` | Создайте `.env` из `.env.example` и задайте пароль. |
| В логах контейнера `libgssapi_krb5.so.2` | Образ собирался без `libgssapi-krb5-2`; в `Dockerfile` он устанавливается, пересоберите образ. |
| `dotnet test` показывает пропущенный тест | Это интеграционный Docker-тест; включается переменной `ANKETAPARSER_DOCKER_TESTS=1`. |
