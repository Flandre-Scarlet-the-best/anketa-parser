"""Проверить отчёт покрытия .NET: атрибуцию исходников, метрики строк и ветвей.

Отчёт формирует официальное MTP-расширение Microsoft.Testing.Extensions.CodeCoverage
в формате Cobertura для одного прогона: unit — все production-сборки решения,
docker — сборки, которые реально загружает тестовый процесс Docker-проверок.
Исключения сгенерированных файлов живут в .github/coverage/dotnet/dotnet-coverage.settings.xml;
проверка не дублирует их, но требует, чтобы рукописные точки входа попадали в отчёт,
а не исчезали молча.
"""

import argparse
import math
import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import cast
from xml.etree import ElementTree

CHECK_ERROR_CODE = 1
UNIT_FLAVOUR = "unit"
DOCKER_FLAVOUR = "docker"
FLAVOURS = frozenset((UNIT_FLAVOUR, DOCKER_FLAVOUR))

# Часть инструментов усекает процент до двух знаков, часть пишет точное отношение:
# допуск покрывает разницу между этими представлениями и запрещает завышение.
RATE_TOLERANCE = 0.0001
MAX_SOURCE_BYTES = 64 * 1024 * 1024
LIMIT_LISTED = 5
DOCKER_TEST_PROJECT = Path(
    "tests/AnketaParser.DockerTests/AnketaParser.DockerTests.csproj"
)
DOCKER_EVIDENCE_PREFIX = "src/AnketaParser.Infrastructure/Persistence/"
ABSOLUTE_PATH = re.compile(r"^(?:[A-Za-z]:/|/)")
BUILD_OUTPUT = re.compile(r"(?:^|/)(?:obj|bin)/")
TYPE_DECLARATION = re.compile(
    r"(?m)^\s*(?:public|internal|file|private|protected|static|sealed|abstract|partial|readonly|unsafe|\s)*(?:class|struct|interface|enum|record|delegate)\b"
)
COMMENT = re.compile(r"//.*$")
USING_DIRECTIVE = re.compile(r"^\s*(?:global\s+)?using\s+.*$")
PREPROCESSOR = re.compile(r"^\s*#.*$")


@dataclass(frozen=True)
class Metrics:
    line_rate: float
    branch_rate: float
    lines_valid: int
    lines_covered: int
    branches_valid: int
    branches_covered: int


@dataclass(frozen=True)
class ClassEntry:
    relative: str
    element: ElementTree.Element


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.directory: Path = Path()
        self.flavour: str = ""
        self.repo_root: Path = Path()
        self.max_age_hours: float = 24.0


def _attribute(element: ElementTree.Element, name: str) -> str:
    value = element.get(name)
    if value is None:
        raise ValueError(f"отсутствует атрибут {name}")
    return value


def _metrics(root: ElementTree.Element) -> Metrics:
    return Metrics(
        line_rate=float(_attribute(root, "line-rate")),
        branch_rate=float(_attribute(root, "branch-rate")),
        lines_valid=int(_attribute(root, "lines-valid")),
        lines_covered=int(_attribute(root, "lines-covered")),
        branches_valid=int(_attribute(root, "branches-valid")),
        branches_covered=int(_attribute(root, "branches-covered")),
    )


def _expected_rates(covered: int, total: int) -> tuple[float, float]:
    """Вернуть (точное, усечённое) представление доли покрытия."""
    if total == 0:
        return 1.0, 1.0
    exact = covered / total
    return exact, math.floor(exact * 10000) / 10000


def _rate_errors(name: str, reported: float, covered: int, total: int) -> list[str]:
    if not 0.0 <= reported <= 1.0:
        return [f"{name}={reported} вне диапазона 0..1"]
    exact, truncated = _expected_rates(covered, total)
    if reported > exact + RATE_TOLERANCE:
        return [f"{name}={reported} завышено относительно пересчёта {exact}"]
    if (
        abs(reported - exact) > RATE_TOLERANCE
        and abs(reported - truncated) > RATE_TOLERANCE
    ):
        return [f"{name}={reported} не совпадает с пересчётом {exact}"]
    return []


def _load_project(project: Path) -> ElementTree.Element:
    text = project.read_text(encoding="utf-8")
    return ElementTree.fromstring(text)  # noqa: S314


def _assembly_name(project: Path) -> str:
    for element in _load_project(project).iter("AssemblyName"):
        value = (element.text or "").strip()
        if value:
            return value
    return project.stem


def _references(project: Path) -> tuple[str, ...]:
    names: list[str] = []
    for element in _load_project(project).iter("ProjectReference"):
        include = element.get("Include")
        if include:
            names.append(Path(include.replace("\\", "/")).stem)
    return tuple(names)


def _required_assemblies(flavour: str, repo_root: Path) -> tuple[tuple[str, ...], ...]:
    """Вернуть (обязательные, допустимые) сборки для указанного прогона."""
    projects = {
        project.stem: project
        for project in sorted((repo_root / "src").glob("*/*.csproj"))
    }
    if not projects:
        raise ValueError("каталог src не содержит проектов")
    allowed = tuple(sorted(_assembly_name(project) for project in projects.values()))
    if flavour == UNIT_FLAVOUR:
        return allowed, allowed
    test_project = repo_root / DOCKER_TEST_PROJECT
    if not test_project.is_file():
        raise ValueError(f"не найден проект Docker-проверок {DOCKER_TEST_PROJECT}")
    pending = list(_references(test_project))
    required: set[str] = set()
    while pending:
        project = projects.get(pending.pop())
        if project is None:
            continue
        assembly = _assembly_name(project)
        if assembly in required:
            continue
        required.add(assembly)
        pending.extend(_references(project))
    return tuple(sorted(required)), allowed


def _select_report(directory: Path) -> tuple[Path | None, list[str]]:
    if not directory.is_dir():
        return None, [f"каталог отчёта отсутствует: {directory}"]
    reports = sorted(path for path in directory.rglob("*.xml") if path.is_file())
    if not reports:
        return None, [f"в каталоге {directory} нет ни одного *.xml"]
    if len(reports) > 1:
        listed = ", ".join(path.name for path in reports[:LIMIT_LISTED])
        return None, [
            (
                f"ожидался один отчёт, найдено {len(reports)}: {listed};"
                " очистите каталог и повторите прогон"
            )
        ]
    return reports[0], []


def _relative_to(candidate: str, root: str) -> str | None:
    lowered = candidate.casefold()
    prefix = root.casefold()
    if lowered == prefix:
        return ""
    if lowered.startswith(prefix + "/"):
        return candidate[len(root) + 1 :]
    return None


def _resolve_source(filename: str, sources: tuple[str, ...], root: str) -> str | None:
    """Разрешить filename отчёта: абсолютный, относительный или от <sources>.

    Отчёт после нормализации путей содержит ``src/...`` без ``<sources>``, поэтому
    относительный путь под ``src`` разрешается напрямую, а не только через
    ``<sources>``.
    """
    normalized = filename.replace("\\", "/")
    if ABSOLUTE_PATH.match(normalized):
        return _relative_to(normalized, root)
    if normalized.startswith("src/") and ".." not in normalized.split("/"):
        return normalized
    for source in sources:
        base = source.rstrip("/")
        if not base:
            continue
        relative = _relative_to(f"{base}/{normalized}", root)
        if relative is not None:
            return relative
    return None


def _freshness_errors(
    root: ElementTree.Element, report: Path, max_age_hours: float | None
) -> list[str]:
    if max_age_hours is None:
        return []
    stamp = root.get("timestamp")
    try:
        moment = int(stamp) if stamp else int(report.stat().st_mtime)
    except ValueError:
        return [f"{report.name}: некорректная метка времени {stamp!r}"]
    age_hours = (time.time() - moment) / 3600
    if age_hours > max_age_hours:
        return [
            (
                f"{report.name}: отчёт устарел ({age_hours:.1f} ч),"
                f" предел {max_age_hours:g} ч"
            )
        ]
    return []


def _package_errors(
    packages: dict[str, ElementTree.Element],
    required: tuple[str, ...],
    allowed: tuple[str, ...],
    namespace: str,
) -> list[str]:
    errors: list[str] = []
    tests = sorted(
        name for name in packages if name.endswith((".Tests", ".DockerTests"))
    )
    if tests:
        errors.append(
            f"{namespace}: в отчёт попали тестовые сборки: {', '.join(tests)}"
        )
    missing = [name for name in required if name not in packages]
    if missing:
        errors.append(
            f"{namespace}: отсутствуют production-сборки: {', '.join(missing)}"
        )
    unexpected = sorted(
        name for name in packages if name not in allowed and name not in tests
    )
    if unexpected:
        errors.append(f"{namespace}: неожиданные сборки: {', '.join(unexpected)}")
    empty = [
        name
        for name in required
        if name in packages and not packages[name].findall("classes/class")
    ]
    if empty:
        errors.append(f"{namespace}: сборки без классов: {', '.join(empty)}")
    return errors


def _class_entries(
    root: ElementTree.Element, sources: tuple[str, ...], root_posix: str, namespace: str
) -> tuple[list[ClassEntry], list[str]]:
    entries: list[ClassEntry] = []
    unresolved: list[str] = []
    artifacts: list[str] = []
    unlabelled = 0
    for element in root.iter("class"):
        filename = element.get("filename")
        if not filename:
            unlabelled += 1
            continue
        relative = _resolve_source(filename, sources, root_posix)
        if relative is None:
            unresolved.append(filename)
            continue
        segments = relative.split("/")
        if not relative.startswith("src/") or ".." in segments or "" in segments:
            unresolved.append(filename)
            continue
        if BUILD_OUTPUT.search(relative):
            artifacts.append(relative)
            continue
        entries.append(ClassEntry(relative, element))
    errors: list[str] = []
    if unlabelled:
        errors.append(f"{namespace}: классов без filename: {unlabelled}")
    if unresolved:
        listed = ", ".join(sorted(set(unresolved))[:LIMIT_LISTED])
        errors.append(f"{namespace}: пути вне src репозитория: {listed}")
    if artifacts:
        listed = ", ".join(sorted(set(artifacts))[:LIMIT_LISTED])
        errors.append(f"{namespace}: пути артефактов сборки: {listed}")
    return entries, errors


def _line_errors(
    entries: list[ClassEntry],
    repo_root: Path,
    namespace: str,
    max_source_bytes: int = MAX_SOURCE_BYTES,
) -> list[str]:
    """Отвергнуть строки вне исходника и явно неисполняемые строки.

    Полная семантика последовательных точек C# требует PDB/Roslyn и здесь не
    воспроизводится, поэтому проверяется только достоверная граница: номер
    строки существует в файле, hits — целое >= 0, а исходник лежит внутри корня
    репозитория и не превышает лимит размера.

    Эвристика «строка пустая или комментарий» сознательно НЕ применяется:
    измерено на реальном отчёте Microsoft.Testing.Extensions.CodeCoverage, что
    он ссылается и на пустые строки внутри многострочных raw-литералов
    (например, SQL в миграциях), поэтому такая проверка давала бы ложные отказы.
    Граница доверия: source-tree приходит из измеренной ревизии как данные,
    ничего из него не исполняется, а семантическая проверка точек следования
    требует PDB и остаётся вне области этой проверки.
    """
    errors: list[str] = []
    for entry in entries:
        source = repo_root / entry.relative
        try:
            resolved = source.resolve(strict=True)
            base = repo_root.resolve()
        except OSError as error:
            errors.append(f"{namespace}: исходник недоступен {entry.relative}: {error}")
            continue
        if not resolved.is_relative_to(base):
            errors.append(
                f"{namespace}: исходник вне корня репозитория: {entry.relative}"
            )
            continue
        size = source.stat().st_size
        if size > max_source_bytes:
            errors.append(
                f"{namespace}: исходник {entry.relative} превышает предел {max_source_bytes} байт ({size})"
            )
            continue
        try:
            text = source.read_text(encoding="utf-8", errors="replace")
            lines = text.splitlines()
        except OSError as error:
            errors.append(
                f"{namespace}: не удалось прочитать {entry.relative}: {error}"
            )
            continue
        for line in entry.element.iter("line"):
            number = (line.get("number") or "").strip()
            hits = (line.get("hits") or "").strip()
            if not number.isdigit() or int(number) < 1:
                errors.append(
                    f"{namespace}: некорректный номер строки в {entry.relative}: {number!r}"
                )
                continue
            if int(number) > len(lines):
                errors.append(
                    f"{namespace}: строка вне исходника {entry.relative}: {number} при {len(lines)} строках файла"
                )

            if not hits.isdigit():
                errors.append(
                    f"{namespace}: некорректные попадания в {entry.relative}: {hits!r}"
                )
    if len(errors) > LIMIT_LISTED:
        return errors[:LIMIT_LISTED] + [
            f"{namespace}: и ещё {len(errors) - LIMIT_LISTED} дефектов строк"
        ]
    return errors


def _docker_evidence_errors(entries: list[ClassEntry], namespace: str) -> list[str]:
    for entry in entries:
        if not entry.relative.startswith(DOCKER_EVIDENCE_PREFIX):
            continue
        for line in entry.element.iter("line"):
            hits = line.get("hits")
            if hits and int(hits) > 0:
                return []
    return [
        (
            f"{namespace}: Docker-отчёт не подтверждает выполнение интеграционных"
            f" проверок — нет покрытых строк под {DOCKER_EVIDENCE_PREFIX}"
        )
    ]


def _has_top_level_statements(text: str) -> bool:
    """Определить файл с операторами верхнего уровня: у него нет объявления типа."""
    if TYPE_DECLARATION.search(text):
        return False
    for line in text.splitlines():
        stripped = COMMENT.sub("", PREPROCESSOR.sub("", line)).strip()
        if not stripped or USING_DIRECTIVE.match(stripped) or stripped == ";":
            continue
        return True
    return False


def _entry_sources(repo_root: Path) -> tuple[tuple[str, str], ...]:
    """Найти рукописные точки входа: их синтезированный тип помечается [CompilerGenerated]."""
    projects = {
        project.parent.name: project
        for project in sorted((repo_root / "src").glob("*/*.csproj"))
    }
    entries: list[tuple[str, str]] = []
    for project in projects.values():
        directory = project.parent
        for path in sorted(directory.rglob("*.cs")):
            relative = path.relative_to(repo_root).as_posix()
            if "/obj/" in relative or "/bin/" in relative:
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            first = text.splitlines()[0] if text.splitlines() else ""
            if "auto-generated" in first:
                continue
            if _has_top_level_statements(text):
                entries.append((relative, _assembly_name(project)))
    return tuple(entries)


def _entry_errors(
    entries: list[ClassEntry],
    required: tuple[str, ...],
    repo_root: Path,
    namespace: str,
) -> list[str]:
    """Потребовать, чтобы рукописные точки входа измеряемого прогона были в отчёте."""
    try:
        expected = _entry_sources(repo_root)
    except (ElementTree.ParseError, OSError) as error:
        return [f"{namespace}: не удалось определить точки входа: {error}"]
    present = {entry.relative for entry in entries}
    missing = sorted(
        relative
        for relative, assembly in expected
        if assembly in required and relative not in present
    )
    if not missing:
        return []
    listed = ", ".join(missing[:LIMIT_LISTED])
    return [
        (
            f"{namespace}: рукописные точки входа исключены из отчёта: {listed};"
            " измерение неполное"
        )
    ]


def inspect(
    directory: Path,
    flavour: str,
    repo_root: Path,
    max_age_hours: float | None = 24.0,
    max_source_bytes: int = MAX_SOURCE_BYTES,
) -> tuple[list[str], str]:
    """Вернуть ошибки проверки и сводку отчёта для журнала."""
    if flavour not in FLAVOURS:
        return [f"неизвестный flavour {flavour!r}; допустимо: {sorted(FLAVOURS)}"], ""
    report, errors = _select_report(directory)
    if report is None:
        return errors, ""
    try:
        text = report.read_text(encoding="utf-8")
        root = ElementTree.fromstring(text)  # noqa: S314
    except (ElementTree.ParseError, OSError, UnicodeDecodeError) as error:
        return [f"повреждённый отчёт {report.name}: {error}"], ""
    if root.tag != "coverage":
        return [f"{report.name}: корневой элемент {root.tag!r} вместо 'coverage'"], ""
    namespace = f"Покрытие .NET ({flavour})"
    try:
        metrics = _metrics(root)
        required, allowed = _required_assemblies(flavour, repo_root)
    except (ElementTree.ParseError, ValueError, OSError) as error:
        return [f"{namespace}: не удалось прочитать отчёт или проекты: {error}"], ""
    errors = _freshness_errors(root, report, max_age_hours)
    if metrics.lines_valid <= 0:
        errors.append(f"{namespace}: измерение пустое, lines-valid=0")
    elif metrics.lines_covered <= 0:
        errors.append(f"{namespace}: ни одна строка не покрыта, lines-covered=0")
    if metrics.branches_valid <= 0:
        errors.append(f"{namespace}: нет данных о ветвях, branches-valid=0")
    elif flavour == UNIT_FLAVOUR and metrics.branch_rate <= 0:
        errors.append(f"{namespace}: branch-rate=0 при branches-valid>0")
    errors.extend(
        _rate_errors(
            f"{namespace}: line-rate",
            metrics.line_rate,
            metrics.lines_covered,
            metrics.lines_valid,
        )
    )
    errors.extend(
        _rate_errors(
            f"{namespace}: branch-rate",
            metrics.branch_rate,
            metrics.branches_covered,
            metrics.branches_valid,
        )
    )
    sources = tuple((source.text or "").strip() for source in root.iter("source"))
    sources = tuple(source for source in sources if source)
    filenames = [element.get("filename") or "" for element in root.iter("class")]
    relative_names = [
        name
        for name in filenames
        if name and not ABSOLUTE_PATH.match(name.replace("\\", "/"))
    ]
    outside_src = [
        name
        for name in relative_names
        if not name.replace("\\", "/").startswith("src/")
    ]
    if not sources and outside_src:
        errors.append(
            f"{namespace}: относительные пути без <sources> не указывают на src: {', '.join(sorted(set(outside_src))[:LIMIT_LISTED])}"
        )
    entries, class_errors = _class_entries(
        root, sources, repo_root.as_posix(), namespace
    )
    errors.extend(class_errors)
    errors.extend(_entry_errors(entries, required, repo_root, namespace))
    errors.extend(_line_errors(entries, repo_root, namespace, max_source_bytes))
    missing = sorted(
        {
            entry.relative
            for entry in entries
            if not (repo_root / entry.relative).is_file()
        }
    )
    if missing:
        listed = ", ".join(missing[:LIMIT_LISTED])
        errors.append(f"{namespace}: отчёт ссылается на отсутствующие файлы: {listed}")
    packages = {package.get("name") or "": package for package in root.iter("package")}
    errors.extend(_package_errors(packages, required, allowed, namespace))
    if flavour == DOCKER_FLAVOUR:
        errors.extend(_docker_evidence_errors(entries, namespace))
    summary = (
        f"{namespace}: {report.name};"
        f" строки {metrics.lines_covered}/{metrics.lines_valid}"
        f" ({metrics.line_rate * 100:.2f}%),"
        f" ветви {metrics.branches_covered}/{metrics.branches_valid}"
        f" ({metrics.branch_rate * 100:.2f}%);"
        f" сборки: {', '.join(sorted(packages))}"
    )
    return errors, summary


def validate(
    directory: Path,
    flavour: str,
    repo_root: Path | None = None,
    max_age_hours: float | None = 24.0,
    max_source_bytes: int = MAX_SOURCE_BYTES,
) -> list[str]:
    """Проверить отчёт покрытия .NET и вернуть список ошибок."""
    errors, _ = inspect(
        directory,
        flavour,
        Path() if repo_root is None else repo_root,
        max_age_hours,
        max_source_bytes,
    )
    return errors


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("directory", type=Path, help="каталог с отчётом Cobertura")
    _ = parser.add_argument(
        "flavour", choices=sorted(FLAVOURS), help="прогон: unit или docker"
    )
    _ = parser.add_argument(
        "--repo-root", type=Path, default=Path(), help="корень репозитория"
    )
    _ = parser.add_argument(
        "--max-source-bytes",
        type=int,
        default=MAX_SOURCE_BYTES,
        help="предельный размер читаемого исходника в байтах",
    )
    _ = parser.add_argument(
        "--max-age-hours",
        type=float,
        default=24.0,
        help="предельный возраст отчёта в часах",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    errors, summary = inspect(
        args.directory.resolve(),
        args.flavour,
        args.repo_root.resolve(),
        args.max_age_hours,
        cast(int, args.max_source_bytes),
    )
    if errors:
        for error in errors:
            print(f"::error::Покрытие .NET не доказано: {error}", file=sys.stderr)
        return CHECK_ERROR_CODE
    print(f"{summary}: проверка отчёта пройдена")
    return 0


if __name__ == "__main__":
    sys.exit(main())
