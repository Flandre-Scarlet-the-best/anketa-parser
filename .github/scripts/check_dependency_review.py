#!/usr/bin/env python3
"""Сверка охвата Dependency Review с фактическими изменениями зависимостей.

Скрипт считает изменения зависимостей между base и head из самих манифестов и
сравнивает их с тем, что опубликовал `actions/dependency-review-action`. Успех
возможен при согласованном нулевом изменении либо подтверждении изменений
пакетов. Ссылки Actions на SHA и версии NuGet перечисляются отдельно как
неподтверждённые: граф зависимостей PR этого репозитория не разрешает ни
закреплённые ссылки Actions, ни версии NuGet, а уязвимости версий NuGet
проверяет собственное задание аудита зависимостей. Для остальных зависимостей
пробел охвата блокирует проверку.
"""

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import cast
from xml.etree import ElementTree

CHECK_FINDINGS_CODE = 1
CHECK_ERROR_CODE = 2
# NuGet использует `packages.<имя проекта>.lock.json`, если такой файл есть рядом
# с проектом, и `packages.lock.json` иначе.
LOCKFILE = re.compile(r"(?:^|/)packages(?:\.[^/]+)?\.lock\.json$")
PROJFILE = re.compile(r"\.csproj$")
REQUIREMENTS = re.compile(r"(?:^|/)requirements(?:[-.][^/]*)?\.txt$")
CENTRAL = re.compile(r"(?:^|/)Directory\.Packages\.props$")
WORKFLOW = re.compile(r"^\.github/workflows/[^/]+\.ya?ml$")
REQUIREMENT = re.compile(
    r"(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*==\s*(?P<version>[^\s;]+)"
)
# YAML допускает значение `uses` в кавычках; кавычки не входят в идентичность.
ACTION_USE = re.compile(
    r"uses:\s*[\"']?(?P<action>[^@\s\"']+)@(?P<ref>[0-9a-f]{40})[\"']?"
)
# GitHub Dependency Graph возвращает для части NuGet-зависимостей диапазон
# вместо разрешённой версии; такое значение не доказывает анализ версии.
# Интервал NuGet начинается с `[` или `(`, поэтому эти символы входят в набор.
IMPRECISE = re.compile(r"^[<>=~^*\[(]")
# Разрешённая версия NuGet: `major.minor[.patch[.revision]]`, необязательные
# суффиксы предварительной версии и метаданных сборки без пустых компонентов.
NUGET_VERSION = re.compile(
    r"^\d+(?:\.\d+){1,3}"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
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
    """Отслеживаемые манифесты в указанной ревизии.

    `-z` обязателен: без него Git экранирует не-ASCII пути, и манифест в
    каталоге с такими символами выпадает из сверки.
    """
    names = git(root, "ls-tree", "-r", "-z", "--name-only", revision).split("\0")
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


def project_files(root: Path, revision: str) -> list[str]:
    """Отслеживаемые проекты MSBuild в указанной ревизии."""
    names = git(root, "ls-tree", "-r", "-z", "--name-only", revision).split("\0")
    return [name for name in names if name and PROJFILE.search(name)]


def project_locks(root: Path, revision: str) -> dict[str, str | None]:
    """Выбранный NuGet lock-файл каждого проекта указанной ревизии.

    Имя `packages.lock.json` не описывает владельца: NuGet предпочитает
    `packages.<имя проекта>.lock.json`, когда этот файл существует, а общий
    lock-файл каталога с несколькими проектами нельзя отнести к одному из них.
    Приоритет именованного файла определяется по файлам конкретной ревизии.
    """
    locks = {name for name in tree_files(root, revision) if LOCKFILE.search(name)}
    projects = [PurePosixPath(name) for name in project_files(root, revision)]
    chosen: dict[str, str | None] = {}
    remaining: dict[str, list[PurePosixPath]] = {}
    for project in projects:
        named = (
            project.parent / f"packages.{project.stem.replace(' ', '_')}.lock.json"
        ).as_posix()
        if named in locks:
            chosen[project.as_posix()] = named
        else:
            remaining.setdefault(project.parent.as_posix(), []).append(project)
    for directory, candidates in remaining.items():
        manifest = (PurePosixPath(directory) / "packages.lock.json").as_posix()
        # Общий файл принадлежит единственному проекту каталога без именованного файла.
        for project in candidates:
            chosen[project.as_posix()] = manifest if len(candidates) == 1 else None
    return chosen


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


def lock_items(content: str) -> list[tuple[str, str, str]]:
    """Записи lock-файла как (пакет, разрешённая версия, тип ссылки)."""
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
    found: list[tuple[str, str, str]] = []
    for entries in cast(dict[str, object], frameworks).values():
        if not isinstance(entries, dict):
            continue
        for package, details in cast(dict[str, object], entries).items():
            if not isinstance(details, dict):
                continue
            item = cast(dict[str, object], details)
            version = item.get("resolved")
            kind = item.get("type")
            if isinstance(version, str) and isinstance(kind, str):
                found.append((package, version, kind))
    return found


def lock_entries(content: str) -> set[tuple[str, str, str]]:
    """Разрешённые версии пакетов из packages.lock.json всех платформ."""
    return {
        (NUGET, package, version)
        for package, version, kind in lock_items(content)
        if kind != "Project"
    }


def direct_dependencies(root: Path, revision: str) -> set[tuple[str, str, str]]:
    """Прямые ссылки ревизии как (пакет в нижнем регистре, версия, манифест)."""
    found: set[tuple[str, str, str]] = set()
    for name in tree_files(root, revision):
        if not LOCKFILE.search(name):
            continue
        for package, version, kind in lock_items(file_text(root, revision, name)):
            if kind == "Direct":
                found.add((package.casefold(), version, name))
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
#
# NuGet-версии граф PR этого репозитория не разрешает: при централизованном
# управлении версиями он видит только .csproj без версий, а packages.lock.json и
# Directory.Packages.props в перечень манифестов графа не попадают. Поэтому
# изменение версии NuGet физически не может быть подтверждено Dependency Review,
# и его отсутствие в отчёте не является пробелом разбора манифестов. Ответственность
# распределена без ослабления: граф подтверждает только экосистемы, которые он
# действительно разрешает, а точные версии NuGet обязано проверять задание аудита
# зависимостей, наличие которого доказывается структурно (см. nuget_audit_proof).
UNCONFIRMED_REASONS = {
    "actions": (
        "GitHub ведёт предупреждения Actions для семантических версий, "
        "а не для закреплённых SHA"
    ),
    "githubactions": (
        "GitHub ведёт предупреждения Actions для семантических версий, "
        "а не для закреплённых SHA"
    ),
    "nuget": (
        "граф не публикует разрешённые версии из packages.lock.json "
        "и Directory.Packages.props"
    ),
}
UNCONFIRMED_ECOSYSTEMS = frozenset(UNCONFIRMED_REASONS)

# Задание аудита обязано проверять уязвимости NuGet вместо графа PR. Отсутствие
# триггера или любой из проверок делает неподтверждённые версии непроверенными,
# поэтому сверка падает, а не сообщает об успехе.
NUGET_AUDIT_WORKFLOW = Path(".github/workflows/dependency-audit.yml")
NUGET_AUDIT_SCRIPTS = ("check_nuget_audit.py", "check_osv.py")
PULL_REQUEST_TRIGGER = "pull_request:"


def verify(expected: list[Change], reported_changes: list[Change]) -> list[str]:
    """Изменения зависимостей из lock-файлов должны быть подтверждены отчётом."""
    if not expected:
        # Нулевое изменение согласовано только при пустом отчёте: иначе
        # обнаруженное действием изменение указывает на пробел в разборе манифестов.
        if reported_changes:
            raise CoverageError(
                "Dependency Review сообщил об изменениях, которых нет в манифестах: "
                + "; ".join(
                    change.describe() for change in reported_changes[:MAX_DIAGNOSTIC]
                )
            )
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
    expected_keys = {
        (
            change.kind,
            change.ecosystem.lower(),
            change.name,
            change.version,
            change.manifest,
        )
        for change in expected
    }
    gaps: list[str] = []
    unconfirmed: list[Change] = []
    for change in expected:
        if change.ecosystem.lower() in UNCONFIRMED_ECOSYSTEMS:
            unconfirmed.append(change)
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
    extras = [
        change
        for change in reported_changes
        if change.ecosystem.lower() not in UNCONFIRMED_ECOSYSTEMS
        and (
            change.kind,
            change.ecosystem.lower(),
            change.name,
            change.version,
            change.manifest,
        )
        not in expected_keys
    ]
    if extras:
        # Обратное расхождение означает, что действие обнаружило изменение,
        # которого нет в разобранных манифестах: охват разбора не доказан.
        raise CoverageError(
            "Dependency Review сообщил об изменениях вне разобранных манифестов: "
            + "; ".join(change.describe() for change in extras[:MAX_DIAGNOSTIC])
        )
    notes = [f"покрытие подтверждено для {len(expected) - len(unconfirmed)} изменений"]
    for ecosystem in sorted({change.ecosystem for change in unconfirmed}):
        entries = [
            change.describe() for change in unconfirmed if change.ecosystem == ecosystem
        ]
        reason = UNCONFIRMED_REASONS.get(ecosystem.lower(), "граф не публикует версию")
        notes.append(
            f"экосистема {ecosystem} не подтверждается графом PR: {reason} "
            f"({len(entries)}): " + "; ".join(entries[:50])
        )
    return notes


def nuget_audit_proof(root: Path) -> str:
    """Доказать, что точные версии NuGet проверяет задание аудита зависимостей.

    Проверка структурная: она подтверждает триггер `pull_request` и состав команд
    задания, но не заменяет результат самих проверок. Задание аудита сканирует все
    lock-файлы дерева PR, поэтому его отказ означает непройденный аудит, а не
    пробел охвата этой сверки.
    """
    path = root / NUGET_AUDIT_WORKFLOW
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise CoverageError(
            f"{NUGET_AUDIT_WORKFLOW}: не удалось прочитать задание аудита: {error}"
        ) from error
    if PULL_REQUEST_TRIGGER not in text:
        raise CoverageError(
            f"{NUGET_AUDIT_WORKFLOW}: аудит зависимостей не запускается на pull_request"
        )
    missing = [script for script in NUGET_AUDIT_SCRIPTS if script not in text]
    if missing:
        raise CoverageError(
            f"{NUGET_AUDIT_WORKFLOW}: точные версии NuGet не проверяются, отсутствует: "
            + ", ".join(missing)
        )
    return (
        "точные версии NuGet проверяет задание аудита зависимостей на pull_request: "
        + ", ".join(NUGET_AUDIT_SCRIPTS)
    )


def exact_version(value: str) -> bool:
    """Значение является точной разрешённой версией NuGet, а не диапазоном."""
    return NUGET_VERSION.fullmatch(value.strip()) is not None


def imprecise(
    reported_changes: list[Change],
    expected: list[Change],
    base_locks: dict[str, str | None],
    head_locks: dict[str, str | None],
    base_direct: set[tuple[str, str, str]] | None = None,
    head_direct: set[tuple[str, str, str]] | None = None,
) -> list[str]:
    """Отчёт с диапазоном версии не доказывает анализ разрешённой версии."""
    lock_changes = {
        (change.kind, change.name.casefold(), change.manifest)
        for change in expected
        if change.ecosystem.lower() == "nuget"
        and LOCKFILE.search(change.manifest)
        and exact_version(change.version)
    }
    # Прямая ссылка с точной версией закрывает пробел и без смены версии: переход
    # Transitive → Direct меняет только тип записи. Транзитивная запись не годится.
    lock_changes |= {
        ("removed", package, manifest)
        for package, version, manifest in base_direct or set()
        if exact_version(version)
    }
    lock_changes |= {
        ("added", package, manifest)
        for package, version, manifest in head_direct or set()
        if exact_version(version)
    }
    gaps: list[str] = []
    for change in reported_changes:
        if not IMPRECISE.match(change.version):
            continue
        project = PurePosixPath(change.manifest)
        # Особое значение графа для PackageReference без версии. Точная версия
        # проверяется отдельным аудитом lock-файла той ревизии, где изменение
        # записано: удалённая версия принадлежит общей точке ветвей, добавленная — head.
        locks = base_locks if change.kind == "removed" else head_locks
        sibling = locks.get(project.as_posix()) if project.suffix == ".csproj" else None
        if (
            change.ecosystem.lower() == "nuget"
            and change.version == ">= 0"
            and sibling is not None
            and (change.kind, change.name.casefold(), sibling) in lock_changes
        ):
            continue
        gaps.append(f"{change.name} {change.version}")
    return gaps


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


def merge_base(root: Path, base: str, head: str) -> str:
    """Общая точка ветвей, с которой Dependency Graph сравнивает head."""
    value = git(root, "merge-base", base, head).strip()
    if not value:
        raise CoverageError("Git не определил общую точку ветвей")
    return value


def expected_changes(root: Path, base: str, head: str) -> list[Change]:
    """Сравнить head с общей точкой, как Dependency Graph API."""
    return changes(
        dependencies(root, merge_base(root, base, head)), dependencies(root, head)
    )


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    try:
        root = args.root.resolve()
        expected = expected_changes(root, args.base, args.head)
        reported_changes = reported(output_text(args))
        notes = verify(expected, reported_changes)
        # Неподтверждённые графом версии NuGet обязаны быть покрыты аудитом —
        # и когда они пришли из манифестов, и когда их обнаружило само действие.
        if any(
            change.ecosystem.lower() == "nuget"
            for change in [*expected, *reported_changes]
        ):
            notes.append(nuget_audit_proof(root))
        gaps = imprecise(
            reported_changes,
            expected,
            project_locks(root, merge_base(root, args.base, args.head)),
            project_locks(root, args.head),
            direct_dependencies(root, merge_base(root, args.base, args.head)),
            direct_dependencies(root, args.head),
        )
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
