# Разработка и проверки

[Все руководства](README.md) · [Архитектура](architecture.md)

Руководство помогает воспроизвести сборку и выбрать проверку, которая доказывает
нужное поведение. Успех модульных тестов не означает живую приёмку Docker.

## Окружение

- Версию SDK берите из [global.json](../global.json). Политика
  `rollForward: latestPatch` допускает совместимый патч той же полосы,
  предварительные версии отключены.
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

Для проверки охвата статического анализа сохраните JSON-отчёт BasedPyright
во временный файл и передайте его в `check_python_coverage.py` вместе с
`.github/pyrightconfig.json`, как в
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

## Покрытие строк и ветвей тестами

В [CI](../.github/workflows/ci.yml) четыре отчёта. У каждого свой набор тестов,
исходников и исполняемых ветвей, поэтому проценты относятся к своему прогону.

| Флаг Codecov | Что измеряется |
| --- | --- |
| `dotnet-unit` | Исполнение производственного кода модульными тестами .NET, включая сборки CLI и Host |
| `dotnet-docker` | Исполнение кода тестовым процессом интеграционных проверок с изолированной PostgreSQL и проверками восстановления. Покрытие кода внутри запущенного контейнера Host сюда не входит |
| `python-linux` | Полный набор `test_*.py` на Linux. Область измерения — скрипты из `.github/scripts` и `.agents/skills`, включая исполнение из дочерних процессов |
| `python-windows` | Та же область скриптов и учёт дочерних процессов, но прогон `test_background_wait.py` на Windows проверяет фоновое ожидание |

### .NET

Из корня репозитория в Bash выполните восстановление и сборку Release из
раздела выше. Для Docker-прогона нужен работающий Docker с Compose v2.
[Настройки MTP](../.github/coverage/dotnet/dotnet-coverage.settings.xml) задают
исключения сгенерированного кода. MTP выпускает два XML за один прогон. В
Cobertura находятся строки, ветви и попадания. Собственный XML Microsoft Code
Coverage содержит диапазоны инструментированного кода, по которым
[валидатор](../.github/scripts/check_dotnet_coverage.py) сверяет состав исходников
по сборкам, включая файлы с нулевым покрытием. Файл с операторами верхнего уровня
обязан присутствовать в отчёте своей сборки; атрибуты, директивы `using` и
объявления типов точкой входа не считаются.

```bash
set -e
shopt -s extglob nullglob
reports=$(mktemp -d)
for suite in unit docker; do
  project=AnketaParser.Tests
  if [[ $suite == docker ]]; then
    project=AnketaParser.DockerTests
    export ANKETAPARSER_DOCKER_TESTS=1
  fi
  dir="$reports/dotnet-$suite"
  dotnet test --project "tests/$project/$project.csproj" -c Release --no-build \
    --results-directory "$dir" -- --coverage \
    --coverage-settings .github/coverage/dotnet/dotnet-coverage.settings.xml
  cobertura=("$dir"/*.cobertura.xml)
  instrumented=("$dir"/!(*.cobertura).xml)
  (( ${#cobertura[@]} == 1 && ${#instrumented[@]} == 1 ))
  python3 .github/scripts/normalize_cobertura_paths.py --report "${cobertura[0]}" --repo-root .
  python3 .github/scripts/check_dotnet_coverage.py "$dir" "$suite" \
    --require-normalized --max-age-hours 24 --instrumented-report "${instrumented[0]}"
done
```

`--max-age-hours` проверяет свежесть только по явному запросу. Для скачанного
артефакта происхождение устанавливают по запуску CI и измеренной ревизии.

### Python

[.coveragerc](../.coveragerc) задаёт область и режим сбора ветвей. Установите
закреплённые зависимости из раздела о контрактах и используйте Python из этого
окружения. На Linux запускайте команды из корня репозитория.
```bash
set -e
COVERAGE_DIR=$(mktemp -d)
export COVERAGE_DIR
.venv/bin/python -m coverage run -m unittest discover -s .github/scripts -p 'test_*.py'
.venv/bin/python -m coverage combine
.venv/bin/python -m coverage lcov -o "$COVERAGE_DIR/lcov.info"
.venv/bin/python .github/scripts/normalize_lcov_paths.py --report "$COVERAGE_DIR/lcov.info" --repo-root .
.venv/bin/python .github/scripts/check_python_coverage_report.py --report "$COVERAGE_DIR/lcov.info" --platform linux --repo-root . --data-dir "$COVERAGE_DIR"
```

На Windows запускайте команды из корня репозитория в PowerShell 7.4 или новее:
`$PSNativeCommandUseErrorActionPreference` в более ранних версиях не включает
обработку ошибок внешних программ, и неудачный `coverage run` не остановит пример.

```powershell
& {
  $ErrorActionPreference = 'Stop'
  $PSNativeCommandUseErrorActionPreference = $true
  $env:COVERAGE_DIR = [IO.Directory]::CreateTempSubdirectory('anketa-coverage-').FullName
  .venv/Scripts/python -m coverage run -m unittest discover -s .github/scripts -p test_background_wait.py -v
  .venv/Scripts/python -m coverage combine
  .venv/Scripts/python -m coverage lcov -o "$env:COVERAGE_DIR/lcov.info"
  .venv/Scripts/python .github/scripts/normalize_lcov_paths.py --report "$env:COVERAGE_DIR/lcov.info" --repo-root .
  .venv/Scripts/python .github/scripts/check_python_coverage_report.py --report "$env:COVERAGE_DIR/lcov.info" --platform windows --repo-root . --data-dir "$env:COVERAGE_DIR"
}
```

[Валидатор LCOV](../.github/scripts/check_python_coverage_report.py) сверяет
исполняемые строки и точки ветвления с исходником тем же парсером закреплённой
версии `coverage`, поэтому число записей `DA` и `BRDA` не принимается на веру.
Для строк с частичной ветвью (`# pragma: no branch`, `while True:`) неисполненные
исходы не пишутся, и там допускается меньшее число записей `BRDA`. Параметр
`--data-dir` проверяет только, что объединённые данные не старше исходников:
происхождение отчёта подтверждают порядок шагов CI и сверка измеренной ревизии.

### Артефакты и публикация

Локальный прогон проверяет измерение и содержимое файлов. CI дополнительно
проверяет результат слияния PR. Для PR из этого репозитория отдельный прогон
измеряет голову PR и сохраняет четыре артефакта с `measured-commit.txt`.
При push в `main` артефакты привязаны к SHA проверенного коммита. Артефакты
хранятся семь дней.

[Доверенный издатель](../.github/workflows/coverage-publisher.yml) получает
артефакты конкретного запуска CI, сверяет SHA измерения с исходниками и
запускает валидаторы из ветки по умолчанию. Состоявшуюся публикацию подтверждают
четыре успешные загрузки на нужный коммит в Codecov. [codecov.yml](../codecov.yml)
запрашивает информационные статусы и ждёт четыре обработанные загрузки
(`after_n_builds`). Счётчик считает загрузки, а не флаги: повторная загрузка
одного отчёта тоже приближает порог, поэтому наличие статусов не доказывает
полноту четырёх групп.

Срок приёма отчётов задан явно как семь дней от времени самого отчёта. Артефакты
живут семь дней от запуска CI, а отчёты публикуются позже, поэтому к концу срока
хранения отчёт может уже не приняться: запас не задаётся, и выравнивание сроков
не обещает приём в последний день. Серверное ограничение Codecov по умолчанию в
12 часов для такого сценария слишком мало. Значение `7d ago` задаёт дату в
прошлом: `7d` парсер сравнения Codecov интерпретирует как будущую дату.

Список файлов для network-секции загрузчик строит из измеренного дерева
`source-tree`, а его плагины отключены: загружаются ровно четыре проверенных
файла, и поиск `.coverage` в дереве ревизии не запускается.

Для публикации нужен корректный `CODECOV_TOKEN` в **Repository Secrets** именно
этого репозитория: `Settings → Secrets and variables → Actions → Repository secrets`.
Если секрет уже существует, дополнительные действия в настройках не нужны.
GitHub Environment и ручное одобрение не требуются. Издатель запускается
автоматически после успешного CI для push в ветку по умолчанию и PR из этого
репозитория; PR из форков не публикуются и токен не получают. Секрет передаётся
только проверке его наличия и четырём шагам загрузки. При отсутствии токена
задание завершается с ошибкой до получения исходников и отчётов.
До появления издателя в ветке по умолчанию артефакты CI подтверждают только
измерение и сохранение файлов. Приём отчётов и появление статусов проверяются
после активации издателя.
[Настройка Codecov](https://docs.codecov.com/docs/quick-start) ·
[Токены Codecov](https://docs.codecov.com/docs/codecov-tokens)

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

zizmor запускается принудительно без сети и без передачи токена, с отказом при
ошибках `parsing` и `collection`. `--no-config` исключает неявную конфигурацию;
учитываются исключения для конкретных строк. Используется стандартный профиль
(`persona`) zizmor `regular` без снижения уровней серьёзности и достоверности. Для
расширенного диагностического аудита можно добавить `--persona auditor`, для
проверки исключений — `--no-ignores`. Код `0` означает отсутствие активных
замечаний, `1–3` — ошибки инструмента, аргументов или пустой охват, `11–14` —
замечания по уровню серьёзности. SARIF и `--no-exit-codes` не используются,
поэтому замечания блокируют задание. Текстовые диагностики не требуют
`security-events: write`.

Статический анализ без сети не проверяет происхождение артефактов и ссылки Actions через внешние сервисы,
не анализирует весь исполняемый код зависимостей и не доказывает безопасность
живого fork PR. Границы доверенного workflow и единственное обоснованное исключение
описаны в [безопасности](security.md).

## Проверка секретов

[Рабочий процесс секретов](../.github/workflows/gitleaks.yml) ставит
закреплённый Gitleaks из официального Release с проверкой SHA256, сначала
прогоняет самопроверку на синтетических фикстурах, затем проверяет диапазон
события через [check_gitleaks.py](../.github/scripts/check_gitleaks.py).
Интеграционные сценарии включаются переменной `GITLEAKS_BINARY`: без неё
модульные тесты проходят, а сценарии с настоящим бинарником пропускаются.

```powershell
$env:GITLEAKS_BINARY = (Get-Command gitleaks).Source
.venv/Scripts/python -m unittest discover -s .github/scripts -p 'test_check_gitleaks.py' -v
.venv/Scripts/python .github/scripts/check_gitleaks.py --audit --repo .
```

Однократный аудит требует полной истории и всех нужных refs: неглубокий клон
не доказывает охват. Значения секретов не публикуются; проверка сообщает только
правило, путь, строку и коммит. Ограничения и реакция на находку описаны в
[безопасности](security.md).

## Аудит зависимостей

Политика аудита принадлежит [Directory.Build.props](../Directory.Build.props):
`NuGetAuditMode=all` и `NuGetAuditLevel=low`. Восстановление с `--force`
гарантирует, что аудит выполняется, а строгая сборка делает найденный advisory
ошибкой. После изменения политики проверьте, что `packages.lock.json` не
изменились.

```powershell
dotnet restore --locked-mode --force
if ($LASTEXITCODE -ne 0) { throw "Восстановление не удалось: отчёт не создаётся" }
dotnet package list --no-restore --vulnerable --include-transitive --format json > "$env:TEMP/nuget-audit.json"
.venv/Scripts/python .github/scripts/check_nuget_audit.py --report "$env:TEMP/nuget-audit.json" --root .
.venv/Scripts/python .github/scripts/check_osv.py --root . --binary <путь>/osv-scanner
```

Проверка кода выхода обязательна: PowerShell не прерывает сценарий после неудачной
внешней команды, а `dotnet package list` без `--no-restore` выполнил бы собственное
восстановление без `--locked-mode` и мог изменить lock-файлы. CI использует тот же
порядок с явным `--no-restore`.

[check_nuget_audit.py](../.github/scripts/check_nuget_audit.py) считает успехом
только отчёт, в котором есть все проекты решения и запрошен транзитивный режим.
[check_osv.py](../.github/scripts/check_osv.py) перечисляет отслеживаемые
манифесты и сверяет каждый пакет с результатом OSV `--all-packages` по источнику,
экосистеме, имени и точной версии. Код выхода без полного инвентаря не доказывает
охват. Частичный или повреждённый отчёт блокируется; локальные ссылки `Project`
отделены от пакетов. Интеграционный тест с `OSV_SCANNER_BINARY` использует настоящий
сканер и проверяет отказ после удаления записи из его отчёта; без этой переменной
тест явно пропускается. Задание OSV в CI задаёт её после проверки SHA256 бинарника.
Закреплённые версии, ограничения покрытия и негативные сценарии описаны в
[безопасности](security.md).

## Политика обновлений Renovate

[renovate.json](../renovate.json) задаёт политику обновлений: одна логическая
зависимость — один PR, `automerge` выключен, `lockFileMaintenance` выключен,
включён Dependency Dashboard и умеренные лимиты PR. Группировка отключена через
`ignorePresets` для `group:monorepos` и `group:recommended`: иначе наследуемые
наборы настроек объединяют пакеты одного монорепозитория, включая `Microsoft.Extensions.*`.
Правила совместимости удерживают .NET SDK в полосе `10.0.x`, `dotnet-ef` в линии
EF 10 и Python в `3.15`, а локальные образы `*:local` исключены из обновлений.
Правило Python сопоставляется по источнику `github-releases`, который Renovate
назначает `actions/setup-python` для `python-version`; имя `python-version`
обозначает вход действия, а не источник данных, и как `matchDatasources` правило
не срабатывает.

[Проверка политики](../.github/workflows/renovate-config.yml) использует
официальный образ `renovate/renovate:44.251.0`, закреплённый digest манифеста, и
выполняет три независимые проверки через
[check_renovate.py](../.github/scripts/check_renovate.py): строгий
`renovate-config-validator`, реальное извлечение со сверкой объявлений каждого
обнаруженного файла и расчёт веток на изолированных фикстурах вместе с
отрицательным контролем группировки. Версия образа проверяется отдельным шагом.
Шаблоны обнаружения движка берутся из журнала `TRACE` перед извлечением и
применяются к отслеживаемым файлам Git; результат сверяется со списком движка.
Путь нового поддерживаемого файла не нужно добавлять в скрипт. Для NuGet,
требований Python, Dockerfile, Compose и GitHub Actions независимо читаются
объявления с сохранением источника, версии и закрепления SHA/digest. Совпадение
имени или префикса версии в другом файле не считается доказательством. Новый
менеджер без независимой сверки, вычисляемое или неподдерживаемое объявление
блокируют подтверждение и требуют расширения проверяющего кода; молчаливого
пропуска нет. Строгая проверка конфигурации отдельно проверяет ссылки на наборы
настроек Renovate.

Workflow запускается на каждом PR и push в `main`, включая добавление манифестов.
Подтверждение полного успешного запуска переиспользуется только при точном
совпадении дерева Git, платформы исполнителя и UTC-дня. Дерево включает пути и содержимое всех отслеживаемых
файлов: изменение, добавление, удаление или переименование любого файла требует
новой проверки. Это консервативная граница, поскольку Renovate обнаруживает
манифесты во всём репозитории. Кэш не восстанавливает файлы и сохраняется только
после успеха версии, strict, extract, lookup и unit-тестов. `workflow_dispatch`
всегда выполняет полный набор, даже при попадании в кэш. Внешние реестры и наборы
настроек могут измениться в течение дня; подтверждение политики не заменяет свежие OSV/NuGet-аудиты.
Кэш PR привязан к его ссылке слияния: повтор этого PR может использовать
сохранённый результат, другой PR и `main` не наследуют его. Полный lookup выполняется максимум двумя процессами
со своими рабочими и кэш-каталогами; `--workers 1` позволяет последовательный
замер. Порядок отчёта не зависит от порядка завершения процессов.

```powershell
docker run --rm -v "${PWD}:/repo" -w /repo --entrypoint sh `
  "renovate/renovate:44.251.0@sha256:64adaabe8ee3e34820c2b044b5f87e281aa278bf53e5d76df2ce800f9cc9407a" `
  -c "python3 .github/scripts/check_renovate.py validate --node '' --renovate /usr/local/sbin/renovate --validator /usr/local/sbin/renovate-config-validator"
```

Локальный запуск использует `RENOVATE_CONFIG_FILE`: платформа `local` не отдаёт
файлы репозитория, поэтому извлечение и lookup берут политику из указанного файла,
а сама настройка проверяется строгим валидатором. Конфигурация и локальные
проверки не доказывают, что Renovate App установлен в репозитории, а канал безопасности
`vulnerabilityAlerts` требует API GitHub и локально не проверяется. Версии
`actionlint`, `zizmor` и ShellCheck остаются ручными: их обновление связано с
пересчётом SHA256 официального архива.

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

Кэш pip сохраняет загрузки по requirements, версии Python и платформе;
установка закреплённых пакетов выполняется и при попадании в кэш. BasedPyright
запускается один раз
с `--outputjson`: `tee` оставляет диагностику в логе и сохраняет отчёт для проверки
охвата, а `pipefail` сохраняет отказ анализатора.

Публикацией, описанием PR и границами кандидатной и доверенной проверок владеет
[git-workflow](../.agents/skills/git-workflow/SKILL.md), выполнением явно
разрешённого ревью — [coderabbit-review](../.agents/skills/coderabbit-review/SKILL.md).
Их процедуры здесь не копируются. Правила агента находятся в
[AGENTS.md](../AGENTS.md), карта исходников — в
[контексте](../.codex/context/INDEX.md). Инструкции и карты английские,
пользовательские тексты и результаты — русские.
