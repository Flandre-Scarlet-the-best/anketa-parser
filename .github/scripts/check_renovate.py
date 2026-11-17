#!/usr/bin/env python3
"""Проверка политики Renovate официальным движком: валидатор, извлечение, ветки.

Три режима доказывают разные вещи: `validate` — конфигурация проходит строгий
валидатор; `extract` — реальное извлечение покрывает отслеживаемые manifests с
теми же версиями, что объявлены в них; `fixtures` — независимые зависимости
получают раздельные ветки, одна зависимость в нескольких файлах остаётся одной
веткой, а отрицательный контроль подтверждает, что проверка различает группировку.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import cast

# Dry-run в режиме local поддерживает только extract и lookup: full недоступен.
EXTRACT_MODE = "extract"
LOOKUP_MODE = "lookup"
EXTRACT_MESSAGE = "Extracted dependencies"
LOOKUP_MESSAGE = "packageFiles with updates"
NUGET_VERSION = re.compile(
    r'<PackageVersion\s+Include="(?P<name>[^"]+)"\s+Version="(?P<version>[^"]+)"'
)
PACKAGE_REFERENCE = re.compile(r'<PackageReference\s+Include="(?P<name>[^"]+)"')
REQUIREMENT = re.compile(
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*==\s*(?P<version>[^\s;]+)"
)
DOCKER_FROM = re.compile(r"^\s*FROM\s+(?P<image>\S+)", re.MULTILINE)
DOCKER_ARG = re.compile(r"^ARG\s+(?P<name>\w+)=(?P<image>\S+)", re.MULTILINE)
COMPOSE_IMAGE = re.compile(r"^\s*image:\s*(?P<image>\S+)\s*$", re.MULTILINE)
ACTION_USE = re.compile(
    r"uses:\s*(?P<action>[^@\s]+)@(?P<ref>\S+)(?:\s+#\s*(?P<comment>v?\S+))?"
)
PYTHON_INPUT = re.compile(r"python-version:\s*'(?P<version>[^']+)'")
MAX_DIAGNOSTIC = 2000


class CoverageError(Exception):
    """Извлечение, конфигурация или ветки не доказывают политику обновлений."""


@dataclass(frozen=True)
class Scenario:
    """Изолированная фикстура с ожидаемым соотношением веток."""

    name: str
    files: dict[str, str]
    deps: tuple[str, ...]
    # distinct — ветки разных зависимостей не совпадают;
    # single — одна зависимость в нескольких файлах даёт совпадающие ветки.
    relation: str


def node_command(node: str, script: Path) -> list[str]:
    """Команда запуска Renovate: node плюс скрипт либо сам исполняемый файл.

    В официальном образе точки входа — исполняемые обёртки, поэтому пустой
    `--node` означает прямой запуск файла без интерпретатора.
    """
    return [str(script)] if not node else [node, str(script)]


def run_script(
    command: list[str],
    cwd: Path,
    environment: dict[str, str] | None = None,
) -> str:
    """Запустить скрипт Renovate; ошибка исполнения — недоказанная проверка."""
    env = dict(os.environ)
    env.update(environment or {})
    try:
        result = subprocess.run(  # noqa: S603
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=cwd,
            env=env,
        )
    except OSError as error:
        raise CoverageError(f"Renovate не запустился: {error}") from error
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()[:MAX_DIAGNOSTIC]
        raise CoverageError(
            f"Renovate завершился с кодом {result.returncode}: {detail}"
        )
    return result.stdout + "\n" + result.stderr


def events(output: str) -> list[dict[str, object]]:
    """Диагностические события Renovate из JSON-лога."""
    parsed: list[dict[str, object]] = []
    for line in output.splitlines():
        content = line.strip()
        if not content.startswith("{"):
            continue
        try:
            event = cast(object, json.loads(content))
        except json.JSONDecodeError:
            continue
        if isinstance(event, dict):
            parsed.append(cast(dict[str, object], event))
    return parsed


def iter_package_files(output: str) -> list[tuple[str, str, list[object]]]:
    """Файлы и зависимости из отчётов extract и lookup официального движка.

    В режиме extract событие содержит раздел `packageFiles`, в режиме lookup —
    разрешённую конфигурацию с тем же разделом. Записи без `packageFile`
    (например, правила конфигурации) отбрасываются.
    """
    found: list[tuple[str, str, list[object]]] = []
    for event in events(output):
        message = event.get("msg")
        source: object = None
        if message == EXTRACT_MESSAGE:
            source = event.get("packageFiles")
        elif message == LOOKUP_MESSAGE:
            source = event.get("config")
        if not isinstance(source, dict):
            continue
        for manager, files in cast(dict[str, object], source).items():
            if not isinstance(files, list):
                continue
            for entry in cast(list[object], files):
                if not isinstance(entry, dict):
                    continue
                item = cast(dict[str, object], entry)
                path = item.get("packageFile")
                deps = item.get("deps")
                if isinstance(path, str) and isinstance(deps, list):
                    found.append((str(manager), path, cast(list[object], deps)))
    return found


def updates(output: str) -> dict[tuple[str, str], tuple[str, ...]]:
    """Ветки обновлений по паре «файл, зависимость» из отчёта lookup."""
    found: dict[tuple[str, str], tuple[str, ...]] = {}
    for _, package_file, deps in iter_package_files(output):
        for dependency in deps:
            collect_branches(found, package_file, dependency)
    return found


def collect_branches(
    found: dict[tuple[str, str], tuple[str, ...]],
    package_file: str,
    dependency: object,
) -> None:
    """Собрать ветки одной зависимости одного файла."""
    if not isinstance(dependency, dict):
        return
    dep = cast(dict[str, object], dependency)
    name = str(dep.get("depName") or dep.get("packageName") or "")
    if not name:
        return
    entries = dep.get("updates")
    if not isinstance(entries, list):
        return
    branches = sorted(
        {
            str(cast(dict[str, object], item).get("branchName", ""))
            for item in cast(list[object], entries)
            if isinstance(item, dict)
        }
        - {""}
    )
    if branches:
        found[(package_file, name)] = tuple(branches)


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise CoverageError(f"Не удалось прочитать {path.name}: {error}") from error


def expected_nuget(root: Path) -> dict[str, str]:
    """Версии из central package management, global.json и dotnet-tools.json."""
    expected: dict[str, str] = {}
    central = root / "Directory.Packages.props"
    if central.is_file():
        for match in NUGET_VERSION.finditer(read_text(central)):
            expected[match.group("name")] = match.group("version")
    global_json = root / "global.json"
    if global_json.is_file():
        data = cast(object, json.loads(read_text(global_json)))
        version = (
            cast(dict[str, object], cast(dict[str, object], data).get("sdk", {})).get(
                "version"
            )
            if isinstance(data, dict)
            else None
        )
        if isinstance(version, str):
            expected["dotnet-sdk"] = version
    tools = root / ".config/dotnet-tools.json"
    if tools.is_file():
        data = cast(object, json.loads(read_text(tools)))
        entries = (
            cast(dict[str, object], data).get("tools")
            if isinstance(data, dict)
            else None
        )
        if isinstance(entries, dict):
            for name, details in cast(dict[str, object], entries).items():
                version = (
                    cast(dict[str, object], details).get("version")
                    if isinstance(details, dict)
                    else None
                )
                if isinstance(version, str):
                    expected[name] = version
    return expected


def expected_pip(root: Path) -> dict[str, str]:
    """Точные версии из файла требований."""
    path = root / ".github/scripts/requirements.txt"
    if not path.is_file():
        return {}
    expected: dict[str, str] = {}
    for line in read_text(path).splitlines():
        content = line.split(" #", 1)[0].strip()
        match = REQUIREMENT.fullmatch(content)
        if match:
            expected[match.group("name")] = match.group("version")
    return expected


def image_name(image: str) -> str:
    """Имя образа без тега и digest."""
    return image.split("@", 1)[0].rsplit(":", 1)[0]


def expected_docker(root: Path) -> dict[str, str]:
    """Внешние образы Dockerfile и Compose с текущим тегом."""
    expected: dict[str, str] = {}
    dockerfile = root / "Dockerfile"
    if dockerfile.is_file():
        content = read_text(dockerfile)
        for match in DOCKER_FROM.finditer(content):
            image = match.group("image")
            if image.startswith("$"):
                continue
            expected[image_name(image)] = image.split(":")[-1]
        for match in DOCKER_ARG.finditer(content):
            image = match.group("image")
            _ = expected.setdefault(image_name(image), image.split(":")[-1])
    compose = root / "compose.yaml"
    if compose.is_file():
        for match in COMPOSE_IMAGE.finditer(read_text(compose)):
            image = match.group("image")
            if image.startswith("$"):
                continue
            # Образы с тегом local собираются из репозитория: Renovate их не
            # обновляет, поэтому они не входят в ожидаемое извлечение.
            if image.split(":")[-1] == "local":
                continue
            _ = expected.setdefault(image_name(image), image.split(":")[-1])
    return expected


def expected_actions(root: Path) -> dict[str, str]:
    """Actions и версии Python из рабочих процессов."""
    expected: dict[str, str] = {}
    directory = root / ".github/workflows"
    if not directory.is_dir():
        return expected
    for path in sorted(directory.glob("*.y*ml")):
        content = read_text(path)
        for match in ACTION_USE.finditer(content):
            # SHA-пин сопровождается комментарием с версией: сверяется она,
            # иначе ожидание пути файла не соответствовало бы извлечению.
            version = match.group("comment") or match.group("ref")
            _ = expected.setdefault(match.group("action"), version)
        for match in PYTHON_INPUT.finditer(content):
            _ = expected.setdefault("python", match.group("version"))
    return expected


def extraction_index(output: str) -> dict[str, list[tuple[str, str]]]:
    """Индекс извлечения: имя зависимости → список пар «файл, версия»."""
    index: dict[str, list[tuple[str, str]]] = {}
    for _, package_file, deps in iter_package_files(output):
        for dependency in deps:
            if not isinstance(dependency, dict):
                continue
            dep = cast(dict[str, object], dependency)
            name = str(dep.get("depName") or dep.get("packageName") or "")
            current = str(dep.get("currentValue") or "")
            if name:
                index.setdefault(name, []).append((package_file, current))
    return index


def verify_expected(
    expected: dict[str, str],
    index: dict[str, list[tuple[str, str]]],
    kind: str,
) -> list[str]:
    """Каждая объявленная зависимость должна быть извлечена с объявленной версией."""
    notes: list[str] = []
    missing: list[str] = []
    mismatched: list[str] = []
    for name, version in sorted(expected.items()):
        entries = index.get(name)
        if not entries:
            missing.append(name)
            continue
        versions = {current.lstrip("=") for _, current in entries}
        if version not in versions and not any(
            current and version.startswith(current) for current in versions
        ):
            mismatched.append(
                f"{name}: ожидалась {version}, извлечено {sorted(versions)}"
            )
        else:
            file_names = sorted({path for path, _ in entries})
            notes.append(f"{kind} {name} {version} из {len(file_names)} файл(ов)")
    if missing:
        raise CoverageError(
            f"{kind}: не извлечены зависимости: {', '.join(sorted(missing))}"
        )
    if mismatched:
        raise CoverageError(
            f"{kind}: версии не совпали с manifests: {'; '.join(mismatched)}"
        )
    return notes


def verify_extraction(root: Path, output: str) -> list[str]:
    """Доказать покрытие всех поддерживаемых менеджеров реальным извлечением."""
    index = extraction_index(output)
    if not index:
        raise CoverageError("Renovate не опубликовал ни одной извлечённой зависимости")
    notes: list[str] = []
    for kind, expected in (
        ("nuget", expected_nuget(root)),
        ("pip", expected_pip(root)),
        ("docker", expected_docker(root)),
        ("actions", expected_actions(root)),
    ):
        if not expected:
            raise CoverageError(f"{kind}: нет объявленных зависимостей для сверки")
        notes.extend(verify_expected(expected, index, kind))
    manifests = sorted(
        {path for entries in index.values() for path, _ in entries if path}
    )
    notes.append(f"manifests: {', '.join(manifests)}")
    return notes


def branches_by_dep(
    found: dict[tuple[str, str], tuple[str, ...]],
) -> dict[str, set[str]]:
    """Все ветки зависимости независимо от файла."""
    result: dict[str, set[str]] = {}
    for (_, name), branches in found.items():
        result.setdefault(name, set()).update(branches)
    return result


def verify_relation(
    found: dict[tuple[str, str], tuple[str, ...]], scenario: Scenario
) -> None:
    """Проверить ожидаемое соотношение веток сценария."""
    by_dep = branches_by_dep(found)
    absent = [name for name in scenario.deps if not by_dep.get(name)]
    if absent:
        raise CoverageError(f"{scenario.name}: нет обновлений для {', '.join(absent)}")
    if scenario.relation == "distinct":
        for name in scenario.deps:
            others = [item for item in scenario.deps if item != name]
            for other in others:
                shared = by_dep[name] & by_dep[other]
                if shared:
                    raise CoverageError(
                        f"{scenario.name}: {name} и {other} объединены в ветку {sorted(shared)}"
                    )
        return
    files = [path for (path, name) in found if name in scenario.deps]
    if len(files) < 2:
        raise CoverageError(
            f"{scenario.name}: зависимость объявлена менее чем в двух файлах"
        )
    for name in scenario.deps:
        per_file = [branches for (_, other), branches in found.items() if other == name]
        if len(set(per_file)) != 1:
            raise CoverageError(
                f"{scenario.name}: {name} обновляется разными ветками: {per_file}"
            )


def write_scenario(directory: Path, scenario: Scenario) -> Path:
    """Создать каталог фикстуры с объявленными файлами."""
    target = directory / scenario.name
    for name, content in scenario.files.items():
        path = target / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(content, encoding="utf-8")
    return target


def project(*packages: tuple[str, str]) -> str:
    """Проектный файл с объявленными зависимостями."""
    references = "\n".join(
        f'    <PackageReference Include="{name}" Version="{version}" />'
        for name, version in packages
    )
    return (
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        "  <PropertyGroup>\n"
        "    <TargetFramework>net10.0</TargetFramework>\n"
        "  </PropertyGroup>\n"
        "  <ItemGroup>\n"
        f"{references}\n"
        "  </ItemGroup>\n"
        "</Project>\n"
    )


SCENARIOS = (
    Scenario(
        "independent-nuget",
        {"app.csproj": project(("Serilog", "3.0.0"), ("Polly", "8.0.0"))},
        ("Serilog", "Polly"),
        "distinct",
    ),
    Scenario(
        "monorepo-nuget",
        {
            "app.csproj": project(
                ("Microsoft.Extensions.Logging", "8.0.0"),
                ("Microsoft.Extensions.Configuration", "8.0.0"),
            )
        },
        ("Microsoft.Extensions.Logging", "Microsoft.Extensions.Configuration"),
        "distinct",
    ),
    Scenario(
        "independent-pip",
        {"requirements.txt": "ruff==0.1.0\nhttpx==0.24.0\n"},
        ("ruff", "httpx"),
        "distinct",
    ),
    Scenario(
        "single-dependency-two-files",
        {
            "first/first.csproj": project(("Serilog", "3.0.0")),
            "second/second.csproj": project(("Serilog", "3.0.0")),
        },
        ("Serilog",),
        "single",
    ),
)
NEGATIVE = Scenario(
    "grouping-control",
    {"app.csproj": project(("Serilog", "3.0.0"), ("Autofac", "7.0.0"))},
    ("Serilog", "Autofac"),
    "distinct",
)


def lookup(
    node: str,
    script: Path,
    directory: Path,
    config_file: Path,
) -> dict[tuple[str, str], tuple[str, ...]]:
    """Запустить lookup в каталоге фикстуры и вернуть рассчитанные ветки."""
    output = run_script(
        [*node_command(node, script), "--platform=local", f"--dry-run={LOOKUP_MODE}"],
        directory,
        {
            "LOG_LEVEL": "debug",
            "LOG_FORMAT": "json",
            "RENOVATE_PLATFORM": "local",
            "RENOVATE_CONFIG_FILE": str(config_file),
        },
    )
    found = updates(output)
    if not found:
        raise CoverageError(f"{directory.name}: lookup не рассчитал ни одной ветки")
    return found


def run_fixtures(node: str, script: Path, config_file: Path) -> list[str]:
    """Все сценарии раздельных веток и одна зависимость в двух файлах."""
    with tempfile.TemporaryDirectory(prefix="renovate-fixtures-") as directory:
        root = Path(directory)
        notes: list[str] = []
        for scenario in SCENARIOS:
            target = write_scenario(root, scenario)
            found = lookup(node, script, target, config_file)
            verify_relation(found, scenario)
            pairs = ", ".join(
                f"{name}→{sorted(branches)}"
                for name, branches in sorted(branches_by_dep(found).items())
            )
            notes.append(f"{scenario.name}: {pairs}")
        return notes


def grouped_config(config_file: Path, target: Path) -> Path:
    """Копия конфигурации с намеренной группировкой для отрицательного контроля."""
    try:
        data = cast(object, json.loads(config_file.read_text(encoding="utf-8")))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise CoverageError(f"Не удалось прочитать конфигурацию: {error}") from error
    if not isinstance(data, dict):
        raise CoverageError("Конфигурация не является объектом")
    config = cast(dict[str, object], data)
    rules = config.get("packageRules")
    if not isinstance(rules, list):
        raise CoverageError("В конфигурации нет packageRules")
    config["ignorePresets"] = []
    config["packageRules"] = [
        {"matchPackageNames": ["Serilog", "Autofac"], "groupName": "grouped-updates"},
        *cast(list[object], rules),
    ]
    _ = target.write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    return target


def negative_control(node: str, script: Path, config_file: Path) -> str:
    """Проверка обязана обнаружить группировку: иначе положительный вывод пуст."""
    with tempfile.TemporaryDirectory(prefix="renovate-negative-") as directory:
        root = Path(directory)
        target = write_scenario(root, NEGATIVE)
        config = grouped_config(config_file, root / "grouped.json")
        found = lookup(node, script, target, config)
        by_dep = branches_by_dep(found)
        shared = by_dep["Serilog"] & by_dep["Autofac"]
        if not shared:
            raise CoverageError(
                "Отрицательный контроль не дал группировки: проверка не различает объединение"
            )
        return f"grouping-control: Serilog и Autofac объединены в {sorted(shared)}"


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.mode: str = "validate"
        self.root: Path = Path(".")
        self.node: str = "node"
        self.renovate: Path | None = None
        self.validator: Path | None = None
        self.config: Path | None = None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("mode", choices=("validate", "extract", "fixtures"))
    _ = parser.add_argument("--root", type=Path, default=Path("."))
    _ = parser.add_argument("--node", default="node")
    _ = parser.add_argument(
        "--renovate",
        type=Path,
        help="dist/renovate.js закреплённого выпуска",
    )
    _ = parser.add_argument(
        "--validator",
        type=Path,
        help="dist/config-validator.js закреплённого выпуска",
    )
    _ = parser.add_argument("--config", type=Path)
    return parser


def require(path: Path | None, name: str) -> Path:
    """Обязательный путь к скрипту официального выпуска."""
    if path is None:
        raise CoverageError(f"Не задан путь к {name}")
    return path


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    root = args.root.resolve()
    config = args.config or root / "renovate.json"
    try:
        if args.mode == "validate":
            output = run_script(
                [
                    *node_command(
                        args.node, require(args.validator, "config-validator.js")
                    ),
                    "--strict",
                    str(config),
                ],
                root,
            )
            print(output.strip()[-MAX_DIAGNOSTIC:])
            print("Renovate: конфигурация прошла строгий валидатор")
            return 0
        if args.mode == "extract":
            output = run_script(
                [
                    *node_command(args.node, require(args.renovate, "renovate.js")),
                    "--platform=local",
                    f"--dry-run={EXTRACT_MODE}",
                ],
                root,
                {
                    "LOG_LEVEL": "debug",
                    "LOG_FORMAT": "json",
                    "RENOVATE_PLATFORM": "local",
                    "RENOVATE_CONFIG_FILE": str(config),
                },
            )
            for note in verify_extraction(root, output):
                print(f"Renovate extract: {note}")
            return 0
        notes = run_fixtures(args.node, require(args.renovate, "renovate.js"), config)
        notes.append(
            negative_control(args.node, require(args.renovate, "renovate.js"), config)
        )
    except CoverageError as error:
        print(f"::error::Проверка Renovate не доказана: {error}", file=sys.stderr)
        return 1
    for note in notes:
        print(f"Renovate fixtures: {note}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
