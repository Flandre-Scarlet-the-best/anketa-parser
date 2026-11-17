#!/usr/bin/env python3
"""Проверка политики Renovate официальным движком: валидатор, извлечение, ветки.

Три режима доказывают разные вещи: `validate` — конфигурация проходит строгий
валидатор; `extract` — реальное извлечение покрывает отслеживаемые манифесты с
теми же версиями, что объявлены в них; `fixtures` — независимые зависимости
получают раздельные ветки, одна зависимость в нескольких файлах остаётся одной
веткой, а отрицательный контроль подтверждает, что проверка различает группировку.
"""

import argparse
import fnmatch
import json
import os
import re
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import cast
from xml.etree import ElementTree

import yaml

# Dry-run в режиме local поддерживает только extract и lookup: full недоступен.
EXTRACT_MODE = "extract"
LOOKUP_MODE = "lookup"
EXTRACT_MESSAGE = "Extracted dependencies"
LOOKUP_MESSAGE = "packageFiles with updates"
REQUIREMENT = re.compile(
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*==\s*(?P<version>[^\s;]+)"
)
DOCKER_ARG = re.compile(r"^ARG\s+(?P<name>\w+)=(?P<image>\S+)", re.MULTILINE)
ACTION_USE = re.compile(
    r"uses:\s*(?P<action>[^@\s]+)@(?P<ref>\S+)(?:\s+#\s*(?P<comment>v?\S+))?"
)
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


@dataclass(frozen=True, order=True)
class Declaration:
    """Идентичность объявления сохраняет менеджер, источник, версию и закрепление."""

    manager: str
    path: str
    name: str
    version: str
    digest: str = ""
    subpath: str = ""


def matched_sources(output: str) -> dict[str, list[str]]:
    """Файлы из стадии обнаружения движка, до извлечения зависимостей.

    TRACE содержит полный список, DEBUG — только число. Отсутствующий или
    неполный список не позволяет доказать охват и блокирует проверку.
    """
    result: dict[str, list[str]] = {}
    counts: dict[str, int] = {}
    for event in events(output):
        match = re.fullmatch(
            r"Matched (\d+) file\(s\) for manager ([\w-]+)(?:: (.*))?",
            str(event.get("msg", "")),
        )
        if not match:
            continue
        count, manager, names = match.groups()
        counts[manager] = int(count)
        if names is not None:
            files = names.split(", ")
            if len(files) != int(count):
                raise CoverageError(
                    f"{manager}: список обнаруженных файлов неоднозначен"
                )
            result[manager] = files
    if not counts or any(
        len(result.get(manager, [])) != count for manager, count in counts.items()
    ):
        raise CoverageError(
            "Нет полного списка обнаруженных манифестов; требуется LOG_LEVEL=trace"
        )
    return result


def source_inventory(root: Path, output: str) -> dict[str, list[str]]:
    """Независимый список Git по действующим шаблонам обнаружения движка.

    Шаблоны берутся до извлечения из закреплённого Renovate; перечень путей
    репозитория не зашивается в проверку. Неизвестный синтаксис не игнорируется.
    """
    result = subprocess.run(  # noqa: S603
        ["git", "ls-files", "-z"],  # noqa: S607
        cwd=root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    if result.returncode:
        raise CoverageError("Git не вернул список отслеживаемых файлов")
    files = [name for name in result.stdout.split("\0") if name]
    patterns: dict[str, list[str]] = {}
    readers = {
        "nuget",
        "pip_requirements",
        "dockerfile",
        "docker-compose",
        "github-actions",
    }
    for event in events(output):
        match = re.fullmatch(
            r"Using file pattern: (.+) for manager ([\w-]+)", str(event.get("msg", ""))
        )
        if match and match[2] in readers:
            patterns.setdefault(match[2], []).append(match[1])
    if set(patterns) != readers:
        raise CoverageError(
            "Нет полного набора шаблонов обнаружения движка; требуется LOG_LEVEL=trace"
        )
    inventory: dict[str, list[str]] = {}
    for manager, entries in patterns.items():
        selected: set[str] = set()
        for pattern in entries:
            if pattern.startswith("/") and pattern.endswith("/"):
                expression = re.compile(pattern[1:-1])
                selected.update(name for name in files if expression.search(name))
            elif not any(character in pattern for character in "{}![]"):
                selected.update(
                    name
                    for name in files
                    if fnmatch.fnmatchcase(name, pattern)
                    or (
                        pattern.startswith("**/")
                        and fnmatch.fnmatchcase(name, pattern[3:])
                    )
                )
            else:
                raise CoverageError(
                    f"{manager}: шаблон требует независимой поддержки: {pattern}"
                )
        inventory[manager] = sorted(selected)
    return inventory


def expected_nuget(root: Path, paths: list[str]) -> set[Declaration]:
    """Объявления во всех обнаруженных проектах, props, targets и файлах SDK/утилит."""
    found: set[Declaration] = set()
    for path in paths:
        text = read_text(root / path)
        if Path(path).name in ("global.json", "dotnet-tools.json"):
            data = cast(dict[str, object], json.loads(text))
            if Path(path).name == "global.json":
                sdk = cast(dict[str, object], data.get("sdk", {}))
                if isinstance(sdk.get("version"), str):
                    found.add(
                        Declaration("nuget", path, "dotnet-sdk", str(sdk["version"]))
                    )
            else:
                for name, item in cast(
                    dict[str, dict[str, str]], data.get("tools", {})
                ).items():
                    found.add(Declaration("nuget", path, name, item["version"]))
            continue
        tree = ElementTree.fromstring(text)  # noqa: S314
        for item in tree.iter():
            if item.tag.rsplit("}", 1)[-1] not in (
                "PackageVersion",
                "PackageReference",
                "GlobalPackageReference",
            ):
                continue
            name = item.get("Include") or item.get("Update")
            if not name:
                raise CoverageError(f"{path}: не определено имя пакета")
            child_versions = {
                child.tag.rsplit("}", 1)[-1]: child.text or "" for child in item
            }
            version = (
                item.get("VersionOverride")
                or child_versions.get("VersionOverride")
                or item.get("Version")
                or child_versions.get("Version")
                or ""
            )
            if "$(" in name or "$(" in version:
                raise CoverageError(
                    f"{path}: вычисляемое объявление {name} требует отдельного доказательства версии"
                )
            found.add(Declaration("nuget", path, name, version))
    return found


def expected_pip(root: Path, paths: list[str]) -> set[Declaration]:
    """Каждая точная версия каждого обнаруженного файла требований."""
    found: set[Declaration] = set()
    for path in paths:
        for line in read_text(root / path).splitlines():
            content = line.split(" #", 1)[0].strip()
            if not content or content.startswith("#"):
                continue
            if content.startswith(("-r ", "-c ", "--requirement ", "--constraint ")):
                # Включённый файл также должен присутствовать в обнаружении движка.
                included = (root / path).parent / content.split(maxsplit=1)[1]
                if (
                    included.resolve().relative_to(root.resolve()).as_posix()
                    not in paths
                ):
                    raise CoverageError(
                        f"{path}: включённый файл не охвачен: {content}"
                    )
                continue
            match = REQUIREMENT.fullmatch(content)
            if not match:
                raise CoverageError(
                    f"{path}: формат требования не позволяет независимо сверить версию: {content}"
                )
            found.add(
                Declaration(
                    "pip_requirements",
                    path,
                    match.group("name"),
                    "==" + match.group("version"),
                )
            )
    return found


def image_declaration(manager: str, path: str, image: str) -> Declaration:
    """Тег и digest образа сверяются независимо, включая адрес реестра с портом."""
    image, _, digest = image.partition("@")
    name, separator, version = image.rpartition(":")
    if not separator or "/" in version:
        name, version = image, ""
    return Declaration(manager, path, name, version, digest)


def expected_docker(root: Path, sources: dict[str, list[str]]) -> set[Declaration]:
    """Внешние образы всех обнаруженных Dockerfile и Compose."""
    found: set[Declaration] = set()
    for manager in ("dockerfile", "docker-compose"):
        for path in sources.get(manager, []):
            content = read_text(root / path)
            if manager == "docker-compose":
                data = cast(dict[str, object], yaml.safe_load(content))
                services = cast(dict[str, dict[str, object]], data.get("services", {}))
                for service in services.values():
                    image = service.get("image")
                    # Compose с build описывает локальную сборку, а не внешний образ.
                    if isinstance(image, str) and "build" not in service:
                        if "$" in image:
                            raise CoverageError(
                                f"{path}: вычисляемый образ {image} требует отдельной сверки"
                            )
                        found.add(image_declaration(manager, path, image))
                continue
            arguments = {
                m.group("name"): m.group("image") for m in DOCKER_ARG.finditer(content)
            }
            stages: set[str] = set()
            syntax = re.search(r"^#\s*syntax=(\S+)", content, re.MULTILINE)
            if syntax:
                found.add(image_declaration(manager, path, syntax[1]))
            for instruction in re.finditer(
                r"^[ \t]*(FROM|COPY)\b([^\r\n]*)", content, re.MULTILINE | re.IGNORECASE
            ):
                kind, body = instruction.groups()
                if "\\" in body or "`" in body:
                    raise CoverageError(
                        f"{path}: многострочная инструкция {kind} требует отдельной сверки"
                    )
                if kind.upper() == "COPY":
                    copy = re.search(r"(?:^|\s)--from=(\S+)", body)
                    if not copy:
                        continue
                    image = copy[1]
                    if "$" in image:
                        raise CoverageError(f"{path}: не разрешён образ {image}")
                    if image not in stages and not image.isdecimal():
                        found.add(image_declaration(manager, path, image))
                    continue
                match = re.match(
                    r"\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?",
                    body,
                    re.IGNORECASE,
                )
                if not match:
                    raise CoverageError(f"{path}: неподдерживаемая инструкция FROM")
                image, stage = match.groups()
                image = re.sub(
                    r"\$\{(\w+)\}|\$(\w+)",
                    lambda m, arguments=arguments: arguments.get(m[1] or m[2], m[0]),
                    image,
                )
                if "$" in image:
                    raise CoverageError(f"{path}: не разрешён образ {image}")
                if image not in stages and image != "scratch":
                    found.add(image_declaration(manager, path, image))
                if stage:
                    stages.add(stage)
    return found


def workflow_images(path: str, content: str) -> set[Declaration]:
    """Образы job container, services и Docker action в YAML workflow."""
    try:
        data = cast(object, yaml.safe_load(content))
    except yaml.YAMLError as error:
        raise CoverageError(f"{path}: повреждённый YAML") from error
    if not isinstance(data, dict):
        raise CoverageError(f"{path}: workflow не является объектом")
    document = cast(dict[str, object], data)
    found: set[Declaration] = set()

    def add_image(value: object) -> None:
        if isinstance(value, dict):
            value = cast(dict[str, object], value).get("image")
        if (
            not isinstance(value, str)
            or not value
            or "$" in value
            or any(c.isspace() for c in value)
        ):
            raise CoverageError(f"{path}: неподдерживаемое объявление образа workflow")
        found.add(image_declaration("github-actions", path, value))

    jobs = document.get("jobs", {})
    if not isinstance(jobs, dict):
        raise CoverageError(f"{path}: неподдерживаемый раздел jobs")
    for job in cast(dict[str, object], jobs).values():
        if not isinstance(job, dict):
            raise CoverageError(f"{path}: неподдерживаемое объявление job")
        entry = cast(dict[str, object], job)
        if "container" in entry:
            add_image(entry["container"])
        if "services" in entry:
            services = entry["services"]
            if not isinstance(services, dict):
                raise CoverageError(f"{path}: неподдерживаемый раздел services")
            for service in cast(dict[str, object], services).values():
                add_image(service)
    runs = document.get("runs")
    if isinstance(runs, dict):
        image = cast(dict[str, object], runs).get("image")
        if isinstance(image, str) and image.lower().startswith("docker://"):
            add_image(image[len("docker://") :])
    return found


def expected_actions(root: Path, paths: list[str]) -> set[Declaration]:
    """Ссылки действий, закрепления и версии среды в каждом workflow/action-файле."""
    found: set[Declaration] = set()
    for path in paths:
        content = read_text(root / path)
        for match in ACTION_USE.finditer(content):
            name, ref = match.group("action"), match.group("ref")
            parts = name.split("/")
            subpath = ""
            if len(parts) > 2 and not name.startswith("docker://"):
                name, subpath = "/".join(parts[:2]), "/".join(parts[2:])
            if re.fullmatch(r"[0-9a-f]{40}", ref):
                found.add(
                    Declaration(
                        "github-actions",
                        path,
                        name,
                        match.group("comment") or "",
                        ref,
                        subpath,
                    )
                )
            else:
                found.add(
                    Declaration("github-actions", path, name, ref, subpath=subpath)
                )
        found.update(workflow_images(path, content))
        runtimes = {
            "python-version": "python",
            "dotnet-version": "dotnet-sdk",
            "node-version": "node",
        }
        for match in re.finditer(
            r"\b([\w-]+-version(?:-file)?):\s*['\"]?([^'\"\s]+)", content
        ):
            if match[1] not in runtimes:
                raise CoverageError(
                    f"{path}: параметр {match[1]} требует независимой сверки"
                )
            name = runtimes[match[1]]
            if "$" in match[2] or match[2].startswith(("[", "|", ">")):
                raise CoverageError(
                    f"{path}: вычисляемая или составная версия требует независимой сверки"
                )
            found.add(Declaration("github-actions", path, name, match[2]))
        for match in re.finditer(
            r"runs-on:\s*['\"]?(ubuntu|windows|macos)-([^'\"\s]+)", content
        ):
            found.add(Declaration("github-actions", path, match[1], match[2]))
    return found


def extraction_index(output: str) -> set[Declaration]:
    """Извлечение сохраняет все версии каждой зависимости в каждом источнике."""
    found: set[Declaration] = set()
    for manager, path, deps in iter_package_files(output):
        for entry in deps:
            if not isinstance(entry, dict):
                raise CoverageError(f"{path}: неизвестный формат зависимости")
            dep = cast(dict[str, object], entry)
            name = str(dep.get("depName") or dep.get("packageName") or "")
            if name:
                subpath = ""
                replacement = dep.get("replaceString")
                if (
                    manager == "github-actions"
                    and dep.get("depType") in ("action", "workflow")
                    and isinstance(replacement, str)
                ):
                    match = re.match(
                        r"['\"]?" + re.escape(name) + r"/([^@\s]+)@", replacement
                    )
                    if match:
                        subpath = match[1]
                found.add(
                    Declaration(
                        manager,
                        path,
                        name,
                        str(dep.get("currentValue") or ""),
                        str(dep.get("currentDigest") or ""),
                        subpath,
                    )
                )
    return found


def verify_expected(
    expected: set[Declaration], index: set[Declaration], kind: str
) -> list[str]:
    """Совпадение в другом файле или с другой версией не доказывает извлечение."""
    missing = expected - index
    if missing:
        raise CoverageError(
            f"{kind}: не извлечены точные объявления: {sorted(missing)}"
        )
    return [
        f"{item.manager} {item.path}: {item.name} {item.version} {item.digest}".rstrip()
        for item in sorted(expected)
    ]


def verify_extraction(
    root: Path, output: str, config_file: Path | None = None
) -> list[str]:
    """Сверить все обнаруженные источники; неизвестный менеджер не получает подтверждения."""
    sources = matched_sources(output)
    inventory = source_inventory(root, output)
    for manager, paths in inventory.items():
        if set(paths) != set(sources.get(manager, [])):
            raise CoverageError(
                f"{manager}: обнаружение не совпало с деревом Git: {paths}"
            )
    # Эти менеджеры обслуживают конфигурацию/правила замен, а не отдельный
    # инвентарь пакетов. Их workflow-файлы уже сверяются github-actions.
    supported = {
        "nuget",
        "pip_requirements",
        "dockerfile",
        "docker-compose",
        "github-actions",
        "renovate-config",
        "jsonata",
        "regex",
    }
    unknown = set(sources) - supported
    if unknown:
        raise CoverageError(
            f"Нет независимой сверки объявлений для менеджеров: {sorted(unknown)}"
        )
    if set(sources.get("jsonata", [])) - set(sources.get("github-actions", [])):
        raise CoverageError(
            "Пользовательский jsonata-менеджер требует независимой сверки объявлений"
        )
    config_path = (root / (config_file or Path("renovate.json"))).resolve()
    if config_path.is_file() and cast(
        dict[str, object], json.loads(read_text(config_path))
    ).get("customManagers"):
        raise CoverageError(
            "Пользовательские менеджеры требуют независимой сверки объявлений"
        )
    if sources.get("regex"):
        raise CoverageError(
            "Пользовательский regex-менеджер требует независимой сверки объявлений"
        )
    index = extraction_index(output)
    expected = (
        expected_nuget(root, sources.get("nuget", []))
        | expected_pip(root, sources.get("pip_requirements", []))
        | expected_docker(root, sources)
        | expected_actions(root, sources.get("github-actions", []))
    )
    if not expected:
        raise CoverageError("Нет объявленных зависимостей для сверки")
    return verify_expected(expected, index, "Renovate")


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
            "RENOVATE_BASE_DIR": str(directory / ".renovate"),
            "RENOVATE_CACHE_DIR": str(directory / ".renovate-cache"),
        },
    )
    found = updates(output)
    if not found:
        raise CoverageError(f"{directory.name}: lookup не рассчитал ни одной ветки")
    return found


def run_scenario(node: str, script: Path, config_file: Path, scenario: Scenario) -> str:
    """Один lookup со своим рабочим каталогом и кэшем движка."""
    with tempfile.TemporaryDirectory(prefix="renovate-fixture-") as directory:
        target = write_scenario(Path(directory), scenario)
        found = lookup(node, script, target, config_file)
        verify_relation(found, scenario)
        pairs = ", ".join(
            f"{name}→{sorted(branches)}"
            for name, branches in sorted(branches_by_dep(found).items())
        )
        return f"{scenario.name}: {pairs}"


def run_fixtures(
    node: str, script: Path, config_file: Path, workers: int = 2
) -> list[str]:
    """Ограничить число процессов; выдавать полный результат в порядке сценариев.

    Будущие результаты читаются все до сообщения об успехе. Исключение любого lookup,
    включая отрицательный контроль, выходит в main; executor ждёт завершения
    остальных процессов до удаления временных каталогов.
    """
    if workers not in (1, 2):
        raise CoverageError("Допустимы только один или два lookup-процесса")
    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = [
            executor.submit(run_scenario, node, script, config_file, scenario)
            for scenario in SCENARIOS
        ]
        futures.append(executor.submit(negative_control, node, script, config_file))
        return [future.result() for future in futures]


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
        shared = by_dep.get("Serilog", set()) & by_dep.get("Autofac", set())
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
        self.workers: int = 2


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
    _ = parser.add_argument(
        "--workers",
        type=int,
        choices=(1, 2),
        default=2,
        help="Число одновременных lookup-процессов; 1 для последовательного замера",
    )
    return parser


def require(path: Path | None, name: str) -> Path:
    """Обязательный путь к скрипту официального выпуска."""
    if path is None:
        raise CoverageError(f"Не задан путь к {name}")
    return path


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    root = args.root.resolve()
    config = (root / (args.config or Path("renovate.json"))).resolve()
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
                    "LOG_LEVEL": "trace",
                    "LOG_FORMAT": "json",
                    "RENOVATE_PLATFORM": "local",
                    "RENOVATE_CONFIG_FILE": str(config),
                },
            )
            for note in verify_extraction(root, output, config):
                print(f"Renovate extract: {note}")
            return 0
        notes = run_fixtures(
            args.node, require(args.renovate, "renovate.js"), config, args.workers
        )
    except (CoverageError, ValueError, ElementTree.ParseError, yaml.YAMLError) as error:
        print(f"::error::Проверка Renovate не доказана: {error}", file=sys.stderr)
        return 1
    for note in notes:
        print(f"Renovate fixtures: {note}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
