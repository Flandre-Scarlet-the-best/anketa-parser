#!/usr/bin/env python3
"""Сверка охвата Dependency Review с фактическими изменениями зависимостей.

Скрипт считает изменения зависимостей между base и head из самих манифестов и
сравнивает их с тем, что опубликовал `actions/dependency-review-action`. Успех
возможен при согласованном нулевом изменении либо подтверждении изменений
пакетов. Ссылки Actions на SHA явно перечисляются отдельно как неподтверждённые;
для остальных зависимостей пробел охвата блокирует проверку.
"""

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import cast
from xml.etree import ElementTree

CHECK_FINDINGS_CODE = 1
CHECK_ERROR_CODE = 2
LOCKFILE = re.compile(r"(?:^|/)packages\.lock\.json$")
REQUIREMENTS = re.compile(r"(?:^|/)requirements(?:[-.][^/]*)?\.txt$")
CENTRAL = re.compile(r"(?:^|/)Directory\.Packages\.props$")
WORKFLOW = re.compile(r"^\.github/workflows/[^/]+\.ya?ml$")
REQUIREMENT = re.compile(
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*==\s*(?P<version>[^\s;]+)"
)
ACTION_USE = re.compile(r"uses:\s*(?P<action>[^@\s]+)@(?P<ref>[0-9a-f]{40})")
# GitHub Dependency Graph возвращает для части NuGet-зависимостей диапазон
# вместо разрешённой версии; такое значение не доказывает анализ версии.
IMPRECISE = re.compile(r"^[<>=~^*]")
MAX_DIAGNOSTIC = 2000
NUGET = "NuGet"
PIP = "pip"
ACTIONS = "actions"


class CoverageError(Exception):
    """Охват Dependency Review не доказан."""


@dataclass(frozen=True, order=True)
class Change:
    """Одно изменение зависимости."""

    kind: str
    ecosystem: str
    name: str
    version: str
    manifest: str

    def describe(self) -> str:
        return (
            f"{self.kind} {self.ecosystem} {self.name} {self.version} ({self.manifest})"
        )


def git(root: Path, *args: str) -> str:
    """Вывод Git или ошибка охвата."""
    result = subprocess.run(  # noqa: S603
        ["git", "-C", str(root), *args],  # noqa: S607
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        raise CoverageError(f"Git не выполнил {args[0]}: {result.stderr.strip()}")
    return result.stdout


def tree_files(root: Path, revision: str) -> list[str]:
    """Отслеживаемые манифесты в указанной ревизии."""
    names = git(root, "ls-tree", "-r", "--name-only", revision).split("\n")
    return [
        name
        for name in names
        if name
        and (
            LOCKFILE.search(name)
            or REQUIREMENTS.search(name)
            or CENTRAL.search(name)
            or WORKFLOW.match(name)
        )
    ]


def file_text(root: Path, revision: str, name: str) -> str:
    """Содержимое файла в ревизии."""
    return git(root, "show", f"{revision}:{name}")


def dependencies(root: Path, revision: str) -> set[tuple[str, str, str, str]]:
    """Зависимости ревизии как (экосистема, имя, версия, манифест)."""
    found: set[tuple[str, str, str, str]] = set()
    for name in tree_files(root, revision):
        content = file_text(root, revision, name)
        if LOCKFILE.search(name):
            found.update((*entry, name) for entry in lock_entries(content))
        elif REQUIREMENTS.search(name):
            for line in content.splitlines():
                line = re.split(r"\s+#", line, maxsplit=1)[0].strip()
                if not line or line.startswith("#"):
                    continue
                match = REQUIREMENT.fullmatch(line)
                if not match:
                    raise CoverageError(f"{name}: неподдерживаемое требование: {line}")
                found.add((PIP, match.group("name"), match.group("version"), name))
        elif CENTRAL.search(name):
            try:
                tree = ElementTree.fromstring(content)  # noqa: S314
            except ElementTree.ParseError as error:
                raise CoverageError(f"{name}: повреждённый XML") from error
            for item in tree.iter():
                if item.tag.rsplit("}", 1)[-1] != "PackageVersion":
                    continue
                package = item.get("Include") or item.get("Update")
                version = item.get("Version")
                if not package or not version:
                    raise CoverageError(f"{name}: неполное объявление PackageVersion")
                found.add((NUGET, package, version, name))
        elif WORKFLOW.match(name):
            found.update(
                (ACTIONS, match.group("action"), match.group("ref"), name)
                for match in ACTION_USE.finditer(content)
            )
    return found


def lock_entries(content: str) -> set[tuple[str, str, str]]:
    """Разрешённые версии пакетов из packages.lock.json всех платформ."""
    try:
        data = cast(object, json.loads(content))
    except json.JSONDecodeError as error:
        raise CoverageError(f"lock-файл в ревизии повреждён: {error}") from error
    frameworks = (
        cast(dict[str, object], data).get("dependencies")
        if isinstance(data, dict)
        else None
    )
    if not isinstance(frameworks, dict):
        raise CoverageError("lock-файл в ревизии не содержит dependencies")
    found: set[tuple[str, str, str]] = set()
    for entries in cast(dict[str, object], frameworks).values():
        if not isinstance(entries, dict):
            continue
        for package, details in cast(dict[str, object], entries).items():
            if not isinstance(details, dict):
                continue
            item = cast(dict[str, object], details)
            if item.get("type") == "Project":
                continue
            version = item.get("resolved")
            if isinstance(version, str):
                found.add((NUGET, package, version))
    return found


def changes(
    base: set[tuple[str, str, str, str]], head: set[tuple[str, str, str, str]]
) -> list[Change]:
    """Замена версии представляется removed/added, как в GitHub API."""
    return sorted(
        [Change("removed", *entry) for entry in base - head]
        + [Change("added", *entry) for entry in head - base]
    )


def reported(text: str) -> list[Change]:
    """Разобрать вывод `dependency-changes` и привести к общему виду."""
    if not text.strip():
        return []
    try:
        data = cast(object, json.loads(text))
    except json.JSONDecodeError as error:
        raise CoverageError(f"Вывод Dependency Review повреждён: {error}") from error
    if not isinstance(data, list):
        raise CoverageError("Вывод Dependency Review не является списком изменений")
    found: list[Change] = []
    for entry in cast(list[object], data):
        if not isinstance(entry, dict):
            raise CoverageError("Изменение Dependency Review имеет неизвестный формат")
        item = cast(dict[str, object], entry)
        fields = [
            item.get(key)
            for key in ("change_type", "ecosystem", "name", "version", "manifest")
        ]
        if any(not isinstance(value, str) or not value for value in fields):
            raise CoverageError(
                "Изменение Dependency Review не содержит обязательных строковых полей"
            )
        if item["change_type"] not in ("added", "removed"):
            raise CoverageError("Неизвестный вид изменения Dependency Review")
        found.append(
            Change(
                str(item["change_type"]),
                str(item["ecosystem"]),
                str(item["name"]),
                str(item["version"]),
                str(item["manifest"]),
            )
        )
    return found


# Граф может перечислять ссылки Actions на SHA, но это не доказывает анализ
# уязвимостей закреплённого кода: GitHub поддерживает alerts для семантических
# версий Actions. Кроме того, снимок PR может не содержать часть новых ссылок.
# Эти изменения перечисляются отдельно даже при наличии записи в графе.
UNCONFIRMED_ECOSYSTEMS = frozenset({"actions", "githubactions"})


def verify(expected: list[Change], reported_changes: list[Change]) -> list[str]:
    """Изменения зависимостей из lock-файлов должны быть подтверждены отчётом."""
    if not expected:
        return ["изменений зависимостей в PR нет; проверять нечего"]
    keys = {
        (
            change.kind,
            change.ecosystem.lower(),
            change.name,
            change.version,
            change.manifest,
        )
        for change in reported_changes
    }
    gaps: list[str] = []
    unconfirmed: list[str] = []
    for change in expected:
        if change.ecosystem.lower() in UNCONFIRMED_ECOSYSTEMS:
            unconfirmed.append(change.describe())
            continue
        if (
            change.kind,
            change.ecosystem.lower(),
            change.name,
            change.version,
            change.manifest,
        ) in keys:
            continue
        gaps.append(change.describe())
    if gaps:
        raise CoverageError(
            "Dependency Review не подтвердил изменения зависимостей: "
            + "; ".join(gaps[:MAX_DIAGNOSTIC])
        )
    notes = [f"покрытие подтверждено для {len(expected) - len(unconfirmed)} изменений"]
    if unconfirmed:
        notes.append(
            "экосистема githubactions не подтверждается графом PR "
            f"({len(unconfirmed)}): " + "; ".join(unconfirmed[:50])
        )
    return notes


def imprecise(reported_changes: list[Change]) -> list[str]:
    """Отчёт с диапазоном версии не доказывает анализ разрешённой версии."""
    return [
        f"{change.name} {change.version}"
        for change in reported_changes
        if IMPRECISE.match(change.version)
    ]


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.root: Path = Path(".")
        self.base: str = ""
        self.head: str = ""
        self.changes_env: str = "CHANGES"
        self.changes_file: Path | None = None


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--root", type=Path, default=Path("."))
    _ = parser.add_argument("--base", required=True)
    _ = parser.add_argument("--head", required=True)
    _ = parser.add_argument("--changes-env", default="CHANGES")
    _ = parser.add_argument("--changes-file", type=Path)
    return parser


def output_text(args: Arguments) -> str:
    """Вывод Action из файла либо переменной окружения."""
    if args.changes_file is not None:
        try:
            return args.changes_file.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as error:
            raise CoverageError(
                f"Не удалось прочитать вывод Action: {error}"
            ) from error
    return os.environ.get(args.changes_env, "")


def expected_changes(root: Path, base: str, head: str) -> list[Change]:
    """Сравнить head с общей точкой, как Dependency Graph API."""
    merge_base = git(root, "merge-base", base, head).strip()
    if not merge_base:
        raise CoverageError("Git не определил общую точку ветвей")
    return changes(dependencies(root, merge_base), dependencies(root, head))


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    try:
        root = args.root.resolve()
        expected = expected_changes(root, args.base, args.head)
        reported_changes = reported(output_text(args))
        notes = verify(expected, reported_changes)
        gaps = imprecise(reported_changes)
    except CoverageError as error:
        print(f"::error::Охват Dependency Review не доказан: {error}", file=sys.stderr)
        return CHECK_ERROR_CODE
    for note in notes:
        print(f"Dependency Review: {note}")
    if gaps:
        print(
            "::error::Dependency Review вернул диапазон версии вместо разрешённой: "
            + ", ".join(gaps[:50]),
            file=sys.stderr,
        )
        return CHECK_FINDINGS_CODE
    return 0


if __name__ == "__main__":
    sys.exit(main())
