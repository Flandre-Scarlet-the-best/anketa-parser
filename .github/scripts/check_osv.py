#!/usr/bin/env python3
"""Проверка известных уязвимостей закреплённым OSV-Scanner по lock-файлам.

Охват доказывается для каждого источника отдельно: инвентарь пакетов считается
из самого manifest, а код выхода сканера сверяется с ожидаемым числом пакетов.
Пустой набор, неразобранный формат, отказ сети и неизвестный код выхода
блокируют проверку вместо ложного успеха.
"""

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import cast

# Коды выхода OSV-Scanner: 0 — пакеты найдены без находок, 1 — есть находки,
# 128 — не найдено ни одного пакета. Остальные значения не доказывают охват.
CLEAN_EXIT_CODE = 0
FINDINGS_EXIT_CODE = 1
NO_PACKAGES_EXIT_CODE = 128
CHECK_FINDINGS_CODE = 1
CHECK_ERROR_CODE = 2
LOCKFILE = re.compile(r"(?:^|/)packages\.lock\.json$")
REQUIREMENTS = re.compile(r"(?:^|/)requirements(?:[-.][^/]*)?\.txt$")
REQUIREMENT_LINE = re.compile(
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*==\s*(?P<version>[^\s;]+)"
)
MAX_DIAGNOSTIC = 2000
NUGET = "nuget"
PIP = "pip"


class CoverageError(Exception):
    """Охват сканирования не доказан: источник, формат или код выхода."""


@dataclass(frozen=True)
class Source:
    """Один manifest с посчитанным инвентарём пакетов."""

    path: str
    ecosystem: str
    packages: frozenset[tuple[str, str]]

    def lockfile_argument(self) -> str:
        """Аргумент --lockfile с явным разбором формата для requirements."""
        if self.ecosystem == PIP:
            return f"requirements.txt:{self.path}"
        return self.path


def tracked_files(root: Path, pattern: str) -> list[str]:
    """Отслеживаемые Git файлы по шаблону; ошибка Git — проблема охвата."""
    result = subprocess.run(  # noqa: S603
        ["git", "-C", str(root), "ls-files", "-z", "--", pattern],  # noqa: S607
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise CoverageError(
            f"Git не вернул отслеживаемые файлы: {result.stderr.strip()}"
        )
    return sorted(name for name in result.stdout.split("\0") if name)


def parse_lockfile(path: Path, name: str) -> Source:
    """Пакеты всех целевых платформ из packages.lock.json, включая транзитивные."""
    try:
        data = cast(object, json.loads(path.read_text(encoding="utf-8")))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise CoverageError(
            f"{name}: не удалось разобрать lock-файл: {error}"
        ) from error
    if not isinstance(data, dict):
        raise CoverageError(f"{name}: lock-файл не является объектом")
    frameworks = cast(dict[str, object], data).get("dependencies")
    if not isinstance(frameworks, dict):
        raise CoverageError(f"{name}: в lock-файле нет раздела dependencies")
    packages: set[tuple[str, str]] = set()
    for framework, entries in cast(dict[str, object], frameworks).items():
        if not isinstance(entries, dict):
            raise CoverageError(f"{name}: некорректный раздел платформы {framework}")
        for package, details in cast(dict[str, object], entries).items():
            if not isinstance(details, dict):
                raise CoverageError(f"{name}: некорректные данные пакета {package}")
            item = cast(dict[str, object], details)
            if item.get("type") == "Project":
                continue
            version = item.get("resolved")
            if not isinstance(version, str) or not version:
                raise CoverageError(
                    f"{name}: у пакета {package} нет разрешённой версии"
                )
            packages.add((package, version))
    return Source(name, NUGET, frozenset(packages))


def parse_requirements(path: Path, name: str) -> Source:
    """Точные версии из requirements.txt; иной формат — недоказанный охват."""
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise CoverageError(
            f"{name}: не удалось прочитать требования: {error}"
        ) from error
    packages: set[tuple[str, str]] = set()
    for number, line in enumerate(text.splitlines(), start=1):
        content = line.split(" #", 1)[0].strip()
        if not content or content.startswith("#"):
            continue
        match = REQUIREMENT_LINE.fullmatch(content)
        if match is None:
            raise CoverageError(
                f"{name}:{number}: требование без точной версии не описывает охват"
            )
        packages.add((match.group("name"), match.group("version")))
    return Source(name, PIP, frozenset(packages))


def inventory(root: Path) -> list[Source]:
    """Все отслеживаемые manifests с посчитанными пакетами."""
    sources = [
        parse_lockfile(root / name, name)
        for name in tracked_files(root, "*packages.lock.json")
    ]
    sources.extend(
        parse_requirements(root / name, name)
        for name in tracked_files(root, "*requirements*.txt")
    )
    if not sources:
        raise CoverageError(
            "Не найдено ни одного manifest: охват проверки не определён"
        )
    return sources


def scanner_command(binary: list[str], source: Source) -> list[str]:
    """Команда сканирования одного источника с явным разбором формата."""
    return [
        *binary,
        "scan",
        "source",
        "--format",
        "json",
        "--lockfile",
        source.lockfile_argument(),
    ]


def run_scanner(binary: list[str], source: Source, root: Path) -> tuple[int, str]:
    """Запустить сканер без shell; вернуть код и сырой вывод."""
    try:
        result = subprocess.run(  # noqa: S603
            scanner_command(binary, source),
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root,
        )
    except OSError as error:
        raise CoverageError(f"OSV-Scanner не запустился: {error}") from error
    return result.returncode, result.stdout


def vulnerabilities(report: str, source: Source) -> list[str]:
    """Разобрать отчёт одного источника и вернуть безопасные строки находок."""
    try:
        data = cast(object, json.loads(report))
    except json.JSONDecodeError as error:
        raise CoverageError(
            f"{source.path}: отчёт сканера повреждён: {error}"
        ) from error
    if not isinstance(data, dict):
        raise CoverageError(f"{source.path}: отчёт сканера не является объектом")
    results = cast(dict[str, object], data).get("results")
    if not isinstance(results, list):
        raise CoverageError(f"{source.path}: в отчёте нет списка результатов")
    found: list[str] = []
    seen: set[str] = set()
    for entry in cast(list[object], results):
        if not isinstance(entry, dict):
            raise CoverageError(f"{source.path}: результат неизвестного формата")
        item = cast(dict[str, object], entry)
        origin = item.get("source")
        path = (
            cast(dict[str, object], origin).get("path")
            if isinstance(origin, dict)
            else None
        )
        if path is not None and not str(path).replace("\\", "/").endswith(source.path):
            raise CoverageError(
                f"{source.path}: отчёт описывает другой источник: {str(path)[:MAX_DIAGNOSTIC]}"
            )
        found.extend(describe_packages(item, source, seen))
    return found


def describe_packages(
    item: dict[str, object], source: Source, seen: set[str]
) -> list[str]:
    """Строки находок по пакетам отчёта без дублей по идентификатору."""
    packages = item.get("packages")
    if not isinstance(packages, list):
        return []
    lines: list[str] = []
    for entry in cast(list[object], packages):
        if not isinstance(entry, dict):
            raise CoverageError(f"{source.path}: пакет результата неизвестного формата")
        package = cast(dict[str, object], entry).get("package")
        details: dict[str, object] = (
            cast(dict[str, object], package) if isinstance(package, dict) else {}
        )
        name = str(details.get("name", "?"))
        version = str(details.get("version", "?"))
        found = cast(dict[str, object], entry).get("vulnerabilities")
        if not isinstance(found, list):
            continue
        for advisory in cast(list[object], found):
            identifier = (
                str(cast(dict[str, object], advisory).get("id", "?"))
                if isinstance(advisory, dict)
                else "?"
            )
            key = f"{identifier}|{source.path}|{name}|{version}"
            if key in seen:
                continue
            seen.add(key)
            lines.append(
                f"{identifier} источник={source.path} пакет={name} версия={version}"
            )
    return lines


def check_source(binary: list[str], source: Source, root: Path) -> list[str]:
    """Проверить один источник: код выхода должен соответствовать инвентарю."""
    expected = len(source.packages)
    code, output = run_scanner(binary, source, root)
    if code not in (CLEAN_EXIT_CODE, FINDINGS_EXIT_CODE, NO_PACKAGES_EXIT_CODE):
        raise CoverageError(
            f"{source.path}: сканер завершился с кодом {code}: {output.strip()[:MAX_DIAGNOSTIC] or 'нет диагностики'}"
        )
    empty = code == NO_PACKAGES_EXIT_CODE
    if empty and expected:
        raise CoverageError(
            f"{source.path}: сканер не нашёл пакетов, а инвентарь содержит {expected}"
        )
    if not empty and not expected:
        raise CoverageError(
            f"{source.path}: сканер нашёл пакеты, а инвентарь пуст: формат разобран неверно"
        )
    if empty:
        return []
    findings = vulnerabilities(output, source)
    if code == FINDINGS_EXIT_CODE and not findings:
        raise CoverageError(f"{source.path}: сканер сообщил о находках без записей")
    if code == CLEAN_EXIT_CODE and findings:
        raise CoverageError(f"{source.path}: отчёт содержит находки при успешном коде")
    return findings


def audit(binary: list[str], root: Path) -> tuple[list[Source], list[str]]:
    """Полный проход по всем источникам с проверкой охвата каждого."""
    sources = inventory(root)
    findings = [
        finding for source in sources for finding in check_source(binary, source, root)
    ]
    total = sum(len(source.packages) for source in sources)
    if not total:
        raise CoverageError("Ни один manifest не содержит пакетов: охват пуст")
    return sources, findings


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.root: Path = Path(".")
        self.binary: Path = Path("osv-scanner")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--root", type=Path, default=Path("."))
    _ = parser.add_argument(
        "--binary",
        type=Path,
        default=Path("osv-scanner"),
        help="путь к закреплённому OSV-Scanner",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    try:
        sources, findings = audit([str(args.binary)], args.root.resolve())
    except CoverageError as error:
        print(f"::error::Проверка уязвимостей не доказана: {error}", file=sys.stderr)
        return CHECK_ERROR_CODE
    for source in sources:
        print(f"OSV: {source.ecosystem} {source.path}: пакетов {len(source.packages)}")
    if not findings:
        print(f"OSV: известных уязвимостей нет; источников {len(sources)}")
        return CLEAN_EXIT_CODE
    for line in findings:
        print(f"::error::Известная уязвимость: {line}", file=sys.stderr)
    print(f"OSV: уязвимостей {len(findings)}", file=sys.stderr)
    return CHECK_FINDINGS_CODE


if __name__ == "__main__":
    sys.exit(main())
