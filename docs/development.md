# Разработка и проверки

[Все руководства](README.md) · [Архитектура](architecture.md)

Руководство помогает воспроизвести сборку и выбрать проверку, которая доказывает
нужное поведение. Успех модульных тестов не означает живую приёмку Docker.

## Окружение

- [global.json](../global.json) выбирает SDK `10.0.402`, `rollForward: latestPatch`,
  без предварительных версий. Нужен совместимый патч полосы `10.0.4xx`;
  произвольный SDK `10.0.x` не гарантирован.
- Проекты SDK-style, `net10.0`; [общие свойства](../Directory.Build.props)
  задают nullable, анализаторы, строгость сборки и файлы фиксации зависимостей.
  [Directory.Packages.props](../Directory.Packages.props) задаёт версии NuGet.
- Python и закреплённые инструменты из
  [requirements.txt](../.github/scripts/requirements.txt) нужны для контрактов.
  Текущий CI выбирает Python `3.15.2`; для точной репетиции используйте его.
- Docker с Compose v2 нужен для контейнерных команд и интеграционной проверки.

Команды .NET ниже подходят для Windows PowerShell и Linux/WSL. В CI используется
Ubuntu; успешное задание на Ubuntu не доказывает отдельную Windows-приёмку.

## Воспроизводимая сборка

Каноническая точка входа — `anketa-parser.slnx`. Из корня:

```sh
dotnet restore --locked-mode
dotnet format anketa-parser.slnx --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
docker compose config --quiet
```

Восстановление в режиме `--locked-mode` отвергает рассинхронизацию зависимостей
и `packages.lock.json`. Не обновляйте файлы фиксации ради зелёной проверки
без изменения зависимостей.
`format --verify-no-changes` проверяет стиль, не переписывая файлы. Сборка должна
пройти строгую политику. Тесты используют Microsoft.Testing.Platform и xUnit v3;
При локальном запуске без ANKETAPARSER_DOCKER_TESTS=1 интеграционные проверки
Docker пропускаются. Следующая успешная команда не отменяет
предыдущий сбой.

## Контракты Python и Markdown

Виртуальное окружение отделяет инструменты от глобального Python. В PowerShell:

```powershell
python -m venv .venv
.venv/Scripts/python -m pip install -r .github/scripts/requirements.txt
.venv/Scripts/python -m unittest discover -s .github/scripts -p 'test_*.py'
.venv/Scripts/python .github/scripts/check_skill_contracts.py
.venv/Scripts/python .github/scripts/check_markdown_links.py
.venv/Scripts/ruff check .github/scripts
.venv/Scripts/ruff format --check .github/scripts
.venv/Scripts/basedpyright --project .github/pyrightconfig.json
```

В Bash/WSL:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r .github/scripts/requirements.txt
.venv/bin/python -m unittest discover -s .github/scripts -p 'test_*.py'
.venv/bin/python .github/scripts/check_skill_contracts.py
.venv/bin/python .github/scripts/check_markdown_links.py
.venv/bin/ruff check .github/scripts
.venv/bin/ruff format --check .github/scripts
.venv/bin/basedpyright --project .github/pyrightconfig.json
```

Для проверки охвата сохраните JSON-отчёт BasedPyright во временный файл и передайте
его в `check_python_coverage.py` вместе с `.github/pyrightconfig.json`, как в
[CI](../.github/workflows/ci.yml). Число файлов вычисляется по исходникам,
поэтому добавление `.py` не требует ручного счётчика. `.venv` и отчёты не коммитятся.

Проверка [Markdown-ссылок](../.github/scripts/check_markdown_links.py) читает
**все отслеживаемые Git файлы `.md`**, включая добавленные в индекс.
Она проверяет существование локальных файлов и каталогов и выход за границы
репозитория, пропускает внешние URL и примеры в коде. Она не проверяет
заголовок после `#`, доступность внешнего сайта, смысл текста, язык или Mermaid.
Отсутствующие определения ссылочных меток не диагностируются: неразрешённая
конструкция может быть обычным текстом. Другие расширения, включая `.markdown`,
в охват не входят; страницы документации в репозитории используют `.md`.
После переноса страниц прочитайте обе навигации:
[человеческую](README.md) и [агентскую](../.codex/context/INDEX.md).

## Проверки GitHub Actions

Независимое задание `Безопасность GitHub Actions` в [CI](../.github/workflows/ci.yml)
проверяет все `.github/workflows/*.yml` и `*.yaml` при `pull_request`, push в
`main` и `merge_group`. Пустой список файлов считается ошибкой; список входов,
версии и результаты каждого файла рабочего процесса видны в логе. Задания .NET и
контрактов не являются зависимостями этого задания.

Используются actionlint `1.7.12`, zizmor `1.32.0` и ShellCheck `0.11.0` из
официальных GitHub Releases. Версии, URL архивов Linux x86_64 и ожидаемые SHA256
закреплены непосредственно в CI; архивы проверяются до распаковки. Бинарные файлы
устанавливаются в `RUNNER_TEMP`, без изменения Python/.NET окружения.
Задание рассчитано на исполнителя GitHub `ubuntu-26.04` x86_64 с Bash, curl, tar
и sha256sum.
ShellCheck установлен явно и подключён к actionlint через `-shellcheck`.
Все задания закреплены на `ubuntu-26.04`. В
[actionlint.yaml](../.github/actionlint.yaml) эта официальная метка исполнителя
GitHub добавлена явно, поскольку справочник actionlint `1.7.12` её ещё не
содержит. Дополнение удаляется после перехода на версию, распознающую метку
самостоятельно; остальные проверки меток исполнителя не отключаются.

Для локального запуска скачайте архивы своей платформы из
[actionlint Releases](https://github.com/rhysd/actionlint/releases/tag/v1.7.12),
[zizmor Releases](https://github.com/zizmorcore/zizmor/releases/tag/v1.32.0) и
[ShellCheck Releases](https://github.com/koalaman/shellcheck/releases/tag/v0.11.0),
сверьте SHA256 с digest соответствующего release asset (для actionlint также
доступен файл checksums), распакуйте в отдельный каталог и добавьте его в PATH
текущего процесса. Для Linux точная процедура установки находится в CI.
Из корня репозитория в PowerShell:

```powershell
actionlint -version
zizmor --version
shellcheck --version
$workflows = @(Get-ChildItem .github/workflows -File | Where-Object Extension -In '.yml', '.yaml' | ForEach-Object FullName)
if ($workflows.Count -eq 0) { throw 'Не найдены файлы рабочих процессов GitHub Actions' }
$workflows
actionlint -no-color -verbose -shellcheck shellcheck @workflows
if ($LASTEXITCODE -ne 0) { throw 'Ошибка actionlint' }
zizmor --offline --strict-collection --no-config --persona regular --format plain --color never --render-links never @workflows
if ($LASTEXITCODE -ne 0) { throw 'Ошибка zizmor' }
```

В Bash используйте массив и команды шага проверки из CI. actionlint проверяет
структуру, выражения, зависимости jobs, известные входы Actions и поддерживаемые
shell-скрипты через ShellCheck. Проверка Python через pyflakes опциональна;
встроенных Python `run` в workflow нет. Проверка метаданных неизвестных удалённых
Actions не заменяет изучение их исходников.

zizmor запускается принудительно offline без передачи токена, с отказом при
ошибках `parsing` и `collection`. `--no-config` исключает неявную конфигурацию;
учитываются адресные inline-исключения. Используется стандартный профиль
(`persona`) zizmor `regular` без снижения уровней серьёзности и достоверности. Для
расширенного диагностического аудита можно добавить `--persona auditor`, для
проверки исключений — `--no-ignores`. Код `0` означает отсутствие активных
замечаний, `1–3` — ошибки инструмента, аргументов или пустой охват, `11–14` —
замечания по уровню серьёзности. SARIF и `--no-exit-codes` не используются,
поэтому замечания блокируют задание. Текстовые диагностики не требуют
`security-events: write`.

Статический offline-анализ не выполняет online-аудиты provenance и ссылок Actions,
не анализирует весь исполняемый код зависимостей и не доказывает безопасность
живого fork PR. Границы доверенного workflow и единственное обоснованное исключение
описаны в [безопасности](security.md).

## Какую проверку выбрать

| Изменение | Что проверять | Что это доказывает |
| --- | --- | --- |
| CLI / JSON / коды | `CliApplicationTests`, сборку и нужный живой вызов CLI | Потоки, сериализацию и отказ на неверных настройках |
| Настройки | `ConfigurationTests`, `ConnectionStringDescriptorTests` | Приоритет окружения, валидацию, описание адреса |
| Агрегация / Host | `DiagnosticsRunnerTests`, `HostApplicationTests` | Статусы, таймаут, отмену, DI и остановку без внешних сервисов |
| Python / skills / маршруты | unittest, Ruff, BasedPyright и его охват, обе проверки контрактов и ссылок | Исполняемые контракты и целостность локальных ссылок |
| Compose / Dockerfile | `compose config`; при необходимости отдельная интеграционная проверка | Конфигурация сама не доказывает соединение и сохранность |
| Документация | Ссылки, сверку с кодом и практический сценарий | Существование адресов и пригодность инструкции |

## Отдельная интеграционная проверка Docker

Тест [ComposeStackIsHealthyAndDatabaseDataSurvivesContainerRecreation](../tests/AnketaParser.DockerTests/ComposeStackSmokeTests.cs)
включается явно. Он строит образ, создаёт случайный Compose-проект и временный
файл переменных окружения, ждёт здоровья сервисов, проверяет отсутствие порта PostgreSQL,
готовность Host, записывает синтетическую строку и проверяет её после принудительного
**пересоздания контейнера PostgreSQL** с сохранением именованного тома. Тест сверяет ID
контейнера до и после операции и повторно проверяет готовность Host.
В завершение удаляет только свой стек вместе с томом и временным файлом окружения;
это не инструкция очищать рабочую БД.

Набор Docker-тестов совместно использует подготовленные ресурсы, не ослабляя
изоляцию проверок:

- общая подготовка собирает `host` и `maintenance`, а проверки повторно используют
  кеш; Compose собирает образ `postgres` при его отсутствии и запускает собственный
  контейнер для проверки стека;
- проверки сохранности продукта работают с отдельным контейнером исходного образа
  `postgres:18.6-alpine`, но каждая получает собственную базу: схемы, права, миграции
  и данные не пересекаются, а базы удаляются при завершении проверки;
- контейнеры Compose, тома, сети, контейнеры PostgreSQL и временные файлы
  принадлежат конкретной проверке и удаляются в её `finally`, включая путь отказа.

PowerShell, с восстановлением прежней переменной:

```powershell
$previousSmoke = $env:ANKETAPARSER_DOCKER_TESTS
try {
    $env:ANKETAPARSER_DOCKER_TESTS = '1'
    dotnet test --project tests/AnketaParser.DockerTests/AnketaParser.DockerTests.csproj -c Release
} finally {
    $env:ANKETAPARSER_DOCKER_TESTS = $previousSmoke
}
```

Bash/WSL:

```bash
ANKETAPARSER_DOCKER_TESTS=1 dotnet test --project tests/AnketaParser.DockerTests/AnketaParser.DockerTests.csproj -c Release
```

Ожидайте прохождение, а не `skipped`. Без рабочего Docker тест выполнить нельзя;
это ограничение следует указать в отчёте.

## CI и процедуры изменения

[ci.yml](../.github/workflows/ci.yml) владеет командами и событиями: push в `main`,
PR с любой базой и `merge_group`. Задание контрактов запускает unittest, Ruff,
BasedPyright с охватом, проверки skills и Markdown; задание сборки — .NET и Compose.
Задание сборки отдельно включает ANKETAPARSER_DOCKER_TESTS=1 и выполняет весь набор
AnketaParser.DockerTests: изолированную PostgreSQL, права и полное восстановление.
Проверка docker compose config валидирует только описание контейнеров и не заменяет
эти тесты.

Публикацией, описанием PR и границами кандидатной и доверенной проверок владеет
[git-workflow](../.agents/skills/git-workflow/SKILL.md), выполнением явно
разрешённого ревью — [coderabbit-review](../.agents/skills/coderabbit-review/SKILL.md).
Их процедуры здесь не копируются. Правила агента находятся в
[AGENTS.md](../AGENTS.md), карта исходников — в
[контексте](../.codex/context/INDEX.md). Инструкции и карты английские,
пользовательские тексты и результаты — русские.
