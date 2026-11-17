#!/usr/bin/env python3
"""Проверка отчёта NuGet Audit: охват проектов, транзитивные пакеты и advisories.

Отчёт создаёт `dotnet package list --vulnerable --include-transitive --format json`
по политике репозитория. Скрипт доказывает, что проверены все проекты решения,
что запрошен транзитивный режим, и перечисляет найденные advisories. Пустой
список проектов, отсутствующий транзитивный режим и повреждённый отчёт
блокируют проверку вместо ложного успеха.
"""

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import cast

CLEAN_EXIT_CODE = 0
CHECK_FINDINGS_CODE = 1
CHECK_ERROR_CODE = 2
TRANSITIVE_FLAG = "--include-transitive"
VULNERABLE_FLAG = "--vulnerable"
MAX_DIAGNOSTIC = 2000


class CoverageError(Exception):
    """Охват аудита не доказан: отчёт, проекты или режим не соответствуют."""


def tracked_projects(root: Path) -> set[str]:
    """Отслеживаемые проектные файлы как относительные пути с прямыми чертами."""
    result = subprocess.run(  # noqa: S603
        ["git", "-C", str(root), "ls-files", "-z", "--", "*.csproj"],  # noqa: S607
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise CoverageError(f"Git не вернул проектные файлы: {result.stderr.strip()}")
    return {name for name in result.stdout.split("\0") if name}


def relative(root: Path, reported: str) -> str:
    """Привести абсолютный путь отчёта к относительному внутри репозитория."""
    normal = reported.replace("\\", "/")
    prefix = root.as_posix().rstrip("/")
    if not normal.startswith(prefix + "/"):
        raise CoverageError(
            f"Отчёт содержит проект вне репозитория: {normal[:MAX_DIAGNOSTIC]}"
        )
    return normal[len(prefix) + 1 :]


def parse_report(text: str, root: Path) -> tuple[list[str], list[str]]:
    """Проверить отчёт и вернуть проекты и строки advisories."""
    try:
        data = cast(object, json.loads(text))
    except json.JSONDecodeError as error:
        raise CoverageError(f"Отчёт NuGet Audit повреждён: {error}") from error
    if not isinstance(data, dict):
        raise CoverageError("Отчёт NuGet Audit не является объектом")
    report = cast(dict[str, object], data)
    if report.get("version") != 1:
        raise CoverageError(f"Неизвестная версия отчёта: {report.get('version')}")
    parameters = report.get("parameters")
    requested = parameters.split() if isinstance(parameters, str) else []
    if VULNERABLE_FLAG not in requested or TRANSITIVE_FLAG not in requested:
        # Без `--vulnerable` в отчёте нет ключа vulnerabilities, поэтому пустой
        # список advisories не доказывает отсутствие уязвимостей.
        raise CoverageError(
            f"Отчёт не подтверждает режим аудита уязвимостей: нужны {VULNERABLE_FLAG} и {TRANSITIVE_FLAG}"
        )
    sources = report.get("sources")
    if not isinstance(sources, list) or not any(
        isinstance(entry, str) for entry in cast(list[object], sources)
    ):
        raise CoverageError("Отчёт не содержит источников vulnerability data")
    projects = report.get("projects")
    if not isinstance(projects, list) or not projects:
        raise CoverageError("Отчёт не содержит ни одного проекта")
    paths: list[str] = []
    advisories: list[str] = []
    for entry in cast(list[object], projects):
        if not isinstance(entry, dict):
            raise CoverageError("Проект отчёта имеет неизвестный формат")
        project = cast(dict[str, object], entry)
        reported = project.get("path")
        if not isinstance(reported, str) or not reported:
            raise CoverageError("Проект отчёта не содержит пути")
        name = relative(root, reported)
        paths.append(name)
        advisories.extend(describe_frameworks(project, name))
    if len(set(paths)) != len(paths):
        raise CoverageError("Отчёт содержит повторяющиеся проекты")
    return paths, advisories


def describe_frameworks(project: dict[str, object], name: str) -> list[str]:
    """Строки advisories по всем платформам и типам пакетов проекта."""
    frameworks = project.get("frameworks")
    if frameworks is None:
        return []
    if not isinstance(frameworks, list):
        raise CoverageError(f"{name}: некорректный раздел платформ")
    lines: list[str] = []
    for entry in cast(list[object], frameworks):
        if not isinstance(entry, dict):
            raise CoverageError(f"{name}: некорректная платформа отчёта")
        framework = cast(dict[str, object], entry)
        for kind, key in (
            ("direct", "topLevelPackages"),
            ("transitive", "transitivePackages"),
        ):
            packages = framework.get(key)
            if packages is None:
                continue
            if not isinstance(packages, list):
                raise CoverageError(f"{name}: некорректный список пакетов {key}")
            lines.extend(describe_packages(cast(list[object], packages), name, kind))
    return lines


def describe_packages(packages: list[object], name: str, kind: str) -> list[str]:
    """Строки advisories по пакетам одного типа; без дублей."""
    lines: list[str] = []
    for entry in packages:
        if not isinstance(entry, dict):
            raise CoverageError(f"{name}: некорректная запись пакета")
        package = cast(dict[str, object], entry)
        if "vulnerabilities" not in package:
            continue
        found = package["vulnerabilities"]
        if not isinstance(found, list):
            raise CoverageError(f"{name}: некорректный список vulnerabilities")
        identifier = str(package.get("id", "?"))
        version = str(package.get("resolvedVersion", "?"))
        for advisory in cast(list[object], found):
            if not isinstance(advisory, dict):
                raise CoverageError(f"{name}: некорректная запись advisory")
            item = cast(dict[str, object], advisory)
            line = f"{item.get('advisoryurl', '?')} проект={name} пакет={identifier} версия={version} severity={item.get('severity', '?')} тип={kind}"
            if line not in lines:
                lines.append(line)
    return lines


def compare_projects(expected: set[str], reported: list[str]) -> None:
    """Каждый проект решения должен быть в отчёте, посторонние — тоже ошибка."""
    if not expected:
        raise CoverageError("В репозитории нет отслеживаемых проектов")
    missing = sorted(expected - set(reported))
    extra = sorted(set(reported) - expected)
    if missing:
        raise CoverageError(f"Аудит не покрывает проекты: {', '.join(missing)}")
    if extra:
        raise CoverageError(f"Отчёт содержит неизвестные проекты: {', '.join(extra)}")


def check(report_path: Path, root: Path) -> tuple[list[str], list[str]]:
    """Проверить отчёт по файлу и подтвердить охват всех проектов решения."""
    try:
        text = report_path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise CoverageError(
            f"Не удалось прочитать отчёт NuGet Audit: {error}"
        ) from error
    projects, advisories = parse_report(text, root)
    compare_projects(tracked_projects(root), projects)
    return projects, advisories


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.report: Path = Path("nuget-audit.json")
        self.root: Path = Path(".")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--report", type=Path, default=Path("nuget-audit.json"))
    _ = parser.add_argument("--root", type=Path, default=Path("."))
    return parser


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    try:
        projects, advisories = check(args.report, args.root.resolve())
    except CoverageError as error:
        print(f"::error::Аудит NuGet не доказан: {error}", file=sys.stderr)
        return CHECK_ERROR_CODE
    print(f"NuGet Audit: проектов проверено {len(projects)}: {', '.join(projects)}")
    if not advisories:
        print("NuGet Audit: известных уязвимостей нет, транзитивные пакеты проверены")
        return CLEAN_EXIT_CODE
    for line in advisories:
        print(f"::error::Известная уязвимость: {line}", file=sys.stderr)
    print(f"NuGet Audit: advisories {len(advisories)}", file=sys.stderr)
    return CHECK_FINDINGS_CODE


if __name__ == "__main__":
    sys.exit(main())
