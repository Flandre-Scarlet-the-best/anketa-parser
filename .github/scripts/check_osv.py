#!/usr/bin/env python3
"""Проверка известных уязвимостей закреплённым OSV-Scanner по lock-файлам.

Охват доказывается для каждого источника отдельно: инвентарь пакетов считается
из самого манифеста и сверяется с JSON, полученным с --all-packages.
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
    """Один манифест с посчитанным инвентарём пакетов."""

    path: str
    ecosystem: str
    packages: frozenset[tuple[str, str]]
    projects: frozenset[str] = frozenset()

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
    projects: set[str] = set()
    for framework, entries in cast(dict[str, object], frameworks).items():
        if not isinstance(entries, dict):
            raise CoverageError(f"{name}: некорректный раздел платформы {framework}")
        for package, details in cast(dict[str, object], entries).items():
            if not isinstance(details, dict):
                raise CoverageError(f"{name}: некорректные данные пакета {package}")
            item = cast(dict[str, object], details)
            if item.get("type") == "Project":
                projects.add(package.lower())
                continue
            version = item.get("resolved")
            if not isinstance(version, str) or not version:
                raise CoverageError(
                    f"{name}: у пакета {package} нет разрешённой версии"
                )
            packages.add((package, version))
    return Source(name, NUGET, frozenset(packages), frozenset(projects))


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
    """Все отслеживаемые манифесты с посчитанными пакетами."""
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
            "Не найдено ни одного манифеста: охват проверки не определён"
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
        "--all-packages",
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


def vulnerabilities(report: str, source: Source, root: Path | None = None) -> list[str]:
    """Сверить инвентарь отчёта и вернуть безопасные строки находок."""
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
    scanned: set[tuple[str, str]] = set()
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
        if not isinstance(path, str) or not same_source(
            path, source.path, root or Path.cwd()
        ):
            raise CoverageError(
                f"{source.path}: отчёт описывает другой источник: {str(path)[:MAX_DIAGNOSTIC]}"
            )
        packages = item.get("packages")
        if not isinstance(packages, list):
            raise CoverageError(f"{source.path}: в результате нет списка пакетов")
        for entry in cast(list[object], packages):
            package = (
                cast(dict[str, object], entry).get("package")
                if isinstance(entry, dict)
                else None
            )
            if not isinstance(package, dict):
                raise CoverageError(f"{source.path}: не определена идентичность пакета")
            details = cast(dict[str, object], package)
            name, version, ecosystem = (
                details.get(key) for key in ("name", "version", "ecosystem")
            )
            if (
                isinstance(name, str)
                and name.lower() in source.projects
                and version == ""
                and ecosystem == "NuGet"
            ):
                continue
            if (
                not isinstance(name, str)
                or not name
                or not isinstance(version, str)
                or not version
                or not isinstance(ecosystem, str)
            ):
                raise CoverageError(f"{source.path}: неполная идентичность пакета")
            if ecosystem.lower() != ("pypi" if source.ecosystem == PIP else NUGET):
                raise CoverageError(
                    f"{source.path}: неожиданная экосистема {ecosystem}"
                )
            scanned.add((canonical_name(name, source), version))
        found.extend(describe_packages(item, source, seen))
    expected = {
        (canonical_name(name, source), version) for name, version in source.packages
    }
    if scanned != expected:
        raise CoverageError(
            f"{source.path}: охват пакетов не подтверждён; "
            + f"отсутствуют {sorted(expected - scanned)}, лишние {sorted(scanned - expected)}"
        )
    return found


def same_source(reported_path: str, expected_path: str, root: Path) -> bool:
    """Абсолютный путь сканера должен соответствовать точному пути в его cwd."""
    # Сканер запускается с cwd репозитория; относительные пути сравниваются целиком.
    actual = Path(reported_path)
    return actual.as_posix() == expected_path or (
        actual.is_absolute() and actual.resolve() == (root / expected_path).resolve()
    )


def canonical_name(name: str, source: Source) -> str:
    """Имена NuGet нечувствительны к регистру; PyPI также нормализует разделители."""
    return (
        re.sub(r"[-_.]+", "-", name).lower()
        if source.ecosystem == PIP
        else name.lower()
    )


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
        if found is None:
            continue
        if not isinstance(found, list):
            raise CoverageError(f"{source.path}: список уязвимостей повреждён")
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
    findings = vulnerabilities(output, source, root)
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
        raise CoverageError("Ни один манифест не содержит пакетов: охват пуст")
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
