"""Проверить отчёт покрытия .NET: атрибуцию исходников, метрики строк и ветвей.

Microsoft.Testing.Extensions.CodeCoverage формирует Cobertura и собственный XML
за один прогон MTP. Собственный XML содержит диапазоны инструментированного кода
и позволяет сверить список исходников без повторения правил сборки и исключений.
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

from normalize_cobertura_paths import MAX_REPORT_BYTES, relative_source

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
BRANCH_COVERAGE = re.compile(
    r"([0-9]{1,3})(?:\.([0-9]{1,2}))?% \(([0-9]{1,10})/([0-9]{1,10})\)"
)
TYPE_DECLARATION = re.compile(
    r"(?m)^\s*(?:public|internal|file|private|protected|static|sealed|abstract|partial|readonly|unsafe|\s)*(?:class|struct|interface|enum|record|delegate)\b"
)
COMMENT = re.compile(r"//.*$")
DOC_COMMENT = re.compile(r"^\s*///.*$")
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
USING_DIRECTIVE = re.compile(r"^\s*(?:global\s+)?using\s+.*$")
USING_STATEMENT = re.compile(r"^\s*(?:await\s+)?using\s+(?:\(|var\b)")
PREPROCESSOR = re.compile(r"^\s*#.*$")
ATTRIBUTE = re.compile(r"^\s*\[")
NAMESPACE_DECLARATION = re.compile(r"^\s*(?:file\s+)?namespace\b")
BRACES_ONLY = re.compile(r"^[{}();]*$")


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
        self.max_age_hours: float | None = None
        self.require_normalized: bool = False
        self.instrumented_report: Path | None = None


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


def _select_report(
    directory: Path, instrumented_report: Path | None
) -> tuple[Path | None, list[str]]:
    if not directory.is_dir():
        return None, [f"каталог отчёта отсутствует: {directory}"]
    reports = sorted(
        path
        for path in directory.rglob("*.xml")
        if path != instrumented_report and path.is_file()
    )
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
    if not math.isfinite(max_age_hours) or max_age_hours < 0:
        return ["--max-age-hours должен быть конечным неотрицательным числом"]
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
    root: ElementTree.Element,
    sources: tuple[str, ...],
    root_posix: str,
    namespace: str,
    *,
    require_normalized: bool,
) -> tuple[list[ClassEntry], list[str]]:
    entries: list[ClassEntry] = []
    unresolved: list[str] = []
    artifacts: list[str] = []
    non_normalized: list[str] = []
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
        if require_normalized and (filename != relative or "." in segments):
            non_normalized.append(filename)
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
    if non_normalized:
        listed = ", ".join(sorted(set(non_normalized))[:LIMIT_LISTED])
        errors.append(f"{namespace}: пути Cobertura не нормализованы: {listed}")
    return entries, errors


def _duplicate_line_numbers(element: ElementTree.Element) -> list[str]:
    """Найти повторные номера строк в разделе классов.

    Раздел ``methods`` повторяет записи классов, поэтому уникальность
    проверяется только по ``lines/line``. Дубликат в самом разделе завышает
    итоги и позволяет подменить сводку согласованными числами.
    """
    seen: set[str] = set()
    duplicates: set[str] = set()
    for line in element.findall("lines/line"):
        number = (line.get("number") or "").strip()
        if number in seen:
            duplicates.add(number)
        seen.add(number)
    return sorted(duplicates)


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
        duplicates = _duplicate_line_numbers(entry.element)
        if duplicates:
            errors.append(
                f"{namespace}: повтор номера строки в {entry.relative}: "
                + ", ".join(duplicates[:LIMIT_LISTED])
            )
        for line in entry.element.iter("line"):
            number = (line.get("number") or "").strip()
            hits = (line.get("hits") or "").strip()
            if not number.isascii() or not number.isdigit() or int(number) < 1:
                errors.append(
                    f"{namespace}: некорректный номер строки в {entry.relative}: {number!r}"
                )
                continue
            if int(number) > len(lines):
                errors.append(
                    f"{namespace}: строка вне исходника {entry.relative}: {number} при {len(lines)} строках файла"
                )

            if not hits.isascii() or not hits.isdigit():
                errors.append(
                    f"{namespace}: некорректные попадания в {entry.relative}: {hits!r}"
                )
    if len(errors) > LIMIT_LISTED:
        return errors[:LIMIT_LISTED] + [
            f"{namespace}: и ещё {len(errors) - LIMIT_LISTED} дефектов строк"
        ]
    return errors


def _line_totals_errors(
    entries: list[ClassEntry], metrics: Metrics, namespace: str
) -> list[str]:
    """Сверить итоги MTP по строкам классов. Раздел методов повторяет эти записи."""
    lines_valid = lines_covered = branches_valid = branches_covered = 0
    for entry in entries:
        for line in entry.element.findall("lines/line"):
            lines_valid += 1
            hits = (line.get("hits") or "").strip()
            lines_covered += hits.isascii() and hits.isdigit() and int(hits) > 0
            coverage = line.get("condition-coverage")
            if coverage is None and (line.get("branch") or "").lower() != "true":
                continue
            match = BRANCH_COVERAGE.fullmatch(coverage or "")
            if match is None:
                return [
                    f"{namespace}: некорректная condition-coverage в {entry.relative}"
                ]
            percent = int(match[1]) * 100 + int((match[2] or "").ljust(2, "0"))
            covered, total = int(match[3]), int(match[4])
            if not 0 <= covered <= total or total == 0 or percent > 10000:
                return [
                    f"{namespace}: некорректная condition-coverage в {entry.relative}"
                ]
            # Math.Round в MTP масштабирует процент до сотых и округляет
            # половины к ближайшему чётному. Конечные нули могут отсутствовать.
            if percent != round(covered / total * 100.0 * 100.0):
                return [
                    f"{namespace}: condition-coverage {coverage} в {entry.relative}"
                    + " не совпадает с долей покрытых ветвей"
                ]
            branches_covered += covered
            branches_valid += total
    errors: list[str] = []
    for name, covered, total, expected_covered, expected_total in (
        (
            "строк",
            lines_covered,
            lines_valid,
            metrics.lines_covered,
            metrics.lines_valid,
        ),
        (
            "ветвей",
            branches_covered,
            branches_valid,
            metrics.branches_covered,
            metrics.branches_valid,
        ),
    ):
        if (covered, total) != (expected_covered, expected_total):
            errors.append(
                f"{namespace}: сводка {name} {expected_covered}/{expected_total}"
                + f" не совпадает с данными классов {covered}/{total}"
            )
    return errors


def _docker_evidence_errors(entries: list[ClassEntry], namespace: str) -> list[str]:
    for entry in entries:
        if not entry.relative.startswith(DOCKER_EVIDENCE_PREFIX):
            continue
        for line in entry.element.findall("lines/line"):
            hits = (line.get("hits") or "").strip()
            if hits.isascii() and hits.isdigit() and int(hits) > 0:
                return []
    return [
        (
            f"{namespace}: Docker-отчёт не подтверждает выполнение интеграционных"
            f" проверок — нет покрытых строк под {DOCKER_EVIDENCE_PREFIX}"
        )
    ]


def _instrumented_sources(
    report: Path, repo_root: Path, allowed: tuple[str, ...]
) -> set[tuple[str, str]]:
    """Собрать исходники с измеренными диапазонами, включая нулевое покрытие."""
    with report.open("rb") as stream:
        raw = stream.read(MAX_REPORT_BYTES + 1)
    if len(raw) > MAX_REPORT_BYTES:
        raise ValueError("превышен допустимый размер XML")
    text = raw.decode("utf-8")
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise ValueError("XML содержит DOCTYPE или ENTITY")
    document = ElementTree.fromstring(text)  # noqa: S314
    if document.tag != "results":
        raise ValueError("ожидался корневой элемент results")
    sources: set[tuple[str, str]] = set()
    for module in document.findall("modules/module"):
        assembly = _attribute(module, "name").removesuffix(".dll")
        if assembly not in allowed:
            continue
        paths = {
            _attribute(element, "id"): _attribute(element, "path")
            for element in module.findall("source_files/source_file")
        }
        instrumented = {
            _attribute(element, "source_id")
            for element in module.findall("functions/function/ranges/range")
        }
        for source_id in sorted(instrumented):
            path = paths.get(source_id)
            if path is None:
                raise ValueError(f"{assembly}: неизвестный source_id {source_id}")
            relative, error = relative_source(path, repo_root)
            if relative is None:
                raise ValueError(f"{assembly}: {error}")
            sources.add((assembly, relative))
    if not sources:
        raise ValueError("нет диапазонов инструментированного кода")
    return sources


def _source_completeness_errors(
    report: Path,
    repo_root: Path,
    allowed: tuple[str, ...],
    entries: list[ClassEntry],
    packages: dict[str, ElementTree.Element],
    namespace: str,
) -> list[str]:
    try:
        expected = _instrumented_sources(report, repo_root, allowed)
    except (ElementTree.ParseError, OSError, ValueError) as error:
        return [f"{namespace}: не удалось сверить XML инструментирования: {error}"]

    measured = {
        entry.element: entry.relative
        for entry in entries
        if entry.element.find("lines/line") is not None
    }
    present = {
        (assembly, measured[element])
        for assembly, package in packages.items()
        for element in package.findall("classes/class")
        if element in measured
    }
    return [
        f"{namespace}: {message}: "
        + ", ".join(
            f"{path} ({assembly})" for assembly, path in sorted(diff)[:LIMIT_LISTED]
        )
        for diff, message in (
            (
                expected - present,
                "инструментированные исходники отсутствуют в Cobertura",
            ),
            (
                present - expected,
                "исходники Cobertura не подтверждены XML инструментирования",
            ),
        )
        if diff
    ]


def _has_top_level_statements(text: str) -> bool:
    """Определить файл с операторами верхнего уровня.

    Операторы верхнего уровня идут до объявлений типов, поэтому файл, который
    начинается с объявления типа или пространства имён, точкой входа не является,
    даже если объявление занимает несколько строк. Атрибуты и директивы ``using``
    не исполняются, а объявление ``partial class`` после операторов не отменяет
    точку входа.
    """
    for line in BLOCK_COMMENT.sub("", text).splitlines():
        stripped = COMMENT.sub(
            "", PREPROCESSOR.sub("", DOC_COMMENT.sub("", line))
        ).strip()
        if not stripped:
            continue
        if USING_STATEMENT.match(stripped):
            # `using var stream = ...;` — исполняемое объявление, а не импорт.
            return True
        if (
            USING_DIRECTIVE.match(stripped)
            or ATTRIBUTE.match(stripped)
            or NAMESPACE_DECLARATION.match(stripped)
            or BRACES_ONLY.match(stripped)
        ):
            continue
        return not TYPE_DECLARATION.match(stripped)
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
            text = path.read_text(encoding="utf-8-sig", errors="replace")
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
    max_age_hours: float | None = None,
    max_source_bytes: int = MAX_SOURCE_BYTES,
    *,
    require_normalized: bool = False,
    instrumented_report: Path | None = None,
) -> tuple[list[str], str]:
    """Вернуть ошибки проверки и сводку отчёта для журнала."""
    if flavour not in FLAVOURS:
        return [f"неизвестный flavour {flavour!r}; допустимо: {sorted(FLAVOURS)}"], ""
    report, errors = _select_report(directory, instrumented_report)
    if report is None:
        return errors, ""
    try:
        with report.open("rb") as stream:
            raw = stream.read(MAX_REPORT_BYTES + 1)
        if len(raw) > MAX_REPORT_BYTES:
            return [f"отчёт {report.name} превышает предел {MAX_REPORT_BYTES} байт"], ""
        text = raw.decode("utf-8")
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
    if require_normalized and root.find(".//source") is not None:
        errors.append(
            f"{namespace}: нормализованный отчёт не должен содержать <source>"
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
        root,
        sources,
        repo_root.as_posix(),
        namespace,
        require_normalized=require_normalized,
    )
    errors.extend(class_errors)
    errors.extend(_entry_errors(entries, required, repo_root, namespace))
    errors.extend(_line_errors(entries, repo_root, namespace, max_source_bytes))
    errors.extend(_line_totals_errors(entries, metrics, namespace))
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
    if instrumented_report is not None:
        errors.extend(
            _source_completeness_errors(
                instrumented_report, repo_root, allowed, entries, packages, namespace
            )
        )
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
    max_age_hours: float | None = None,
    max_source_bytes: int = MAX_SOURCE_BYTES,
    *,
    require_normalized: bool = False,
    instrumented_report: Path | None = None,
) -> list[str]:
    """Проверить отчёт покрытия .NET и вернуть список ошибок."""
    root = Path() if repo_root is None else repo_root
    errors, _ = inspect(
        directory,
        flavour,
        root.resolve(),
        max_age_hours,
        max_source_bytes,
        require_normalized=require_normalized,
        instrumented_report=instrumented_report,
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
        help="необязательный предельный возраст отчёта в часах для свежего прогона",
    )
    _ = parser.add_argument(
        "--require-normalized",
        action="store_true",
        help="требовать исходные пути src/... без <sources>",
    )
    _ = parser.add_argument(
        "--instrumented-report",
        type=Path,
        help="собственный XML Microsoft Code Coverage из того же прогона",
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
        require_normalized=args.require_normalized,
        instrumented_report=(
            args.instrumented_report.resolve() if args.instrumented_report else None
        ),
    )
    if errors:
        for error in errors:
            print(f"::error::Покрытие .NET не доказано: {error}", file=sys.stderr)
        return CHECK_ERROR_CODE
    print(f"{summary}: проверка отчёта пройдена")
    return 0


if __name__ == "__main__":
    sys.exit(main())
