# anketa-parser

Инфраструктурная основа на C#/.NET 10: долгоживущий Host, диагностический CLI,
единая конфигурация и Docker Compose с PostgreSQL.

Работают начальная диагностика, периодическая проверка готовности, структурированные
логи и штатная остановка. CLI предоставляет `help`, `version`, `doctor` и JSON.
Compose сохраняет данные PostgreSQL в именованном томе и по умолчанию не публикует
порты. Есть модульные тесты и отдельно включаемая интеграционная проверка Docker.

Парсинг анкет, Telegram, LLM, доменная схема БД, миграции и HTTP API пока не
реализованы. PostgreSQL проверяется запросом `SELECT 1`.

## Быстрый старт

Нужен SDK, совместимый с [global.json](global.json): `10.0.402` с
`latestPatch`. Из корня в PowerShell или Bash/WSL:

```sh
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet src/AnketaParser.Cli/bin/Release/net10.0/AnketaParser.Cli.dll doctor --json
dotnet run --project src/AnketaParser.Host --no-restore
```

Без настроенной БД `doctor` возвращает `status: degraded`, пробу `skipped` и код
`0`: конфигурация допустима, готовность БД не заявляется. Host остаётся работать;
остановка — `Ctrl+C`. Без аргументов CLI показывает справку.

Для настоящего соединения с PostgreSQL используйте
[сценарий Docker](docs/operations.md); для диагностики ошибок —
[CLI и готовность](docs/diagnostics.md).

## Руководства

| Тема | Где читать |
| --- | --- |
| Все пользовательские задачи | [Документация](docs/README.md) |
| Компоненты и зависимости | [Архитектура](docs/architecture.md) |
| Команды, JSON, статусы и коды | [Диагностика](docs/diagnostics.md) |
| Источники, ключи, локальные значения | [Конфигурация](docs/configuration.md) |
| Локальный запуск, Docker, остановка и ошибки | [Эксплуатация](docs/operations.md) |
| Сборка, тесты и CI | [Разработка](docs/development.md) |
| Секреты, логи и границы реализации | [Безопасность](docs/security.md) |

Обязательные правила агента — в [AGENTS.md](AGENTS.md), компактная карта
исходников и проверок — в [.codex/context/INDEX.md](.codex/context/INDEX.md).
Процедуры публикации и явно разрешённого ревью принадлежат
[git-workflow](.agents/skills/git-workflow/SKILL.md) и
[coderabbit-review](.agents/skills/coderabbit-review/SKILL.md).
Инструкции и агентские карты написаны на английском; документация для людей,
ответы, коммиты и PR — на русском.
