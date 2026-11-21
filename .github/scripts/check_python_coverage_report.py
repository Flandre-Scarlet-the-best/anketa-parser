"""Проверить LCOV-отчёт runtime-покрытия Python и атрибуцию работы subprocess.

Валидатор работает по уже нормализованному отчёту: CRLF, обратные слэши,
абсолютные пути и ``..`` считаются ошибкой, а не «исправляются» молча.
Ожидаемый набор файлов всегда вычисляется из git (отслеживаемые плюс новые
файлы, ещё не попавшие в индекс), поэтому число файлов не зашивается, а
неисполненные файлы обязаны присутствовать в отчёте с нулевыми попаданиями.
"""

from __future__ import annotations

import argparse
import ast
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import cast

import coverage
from coverage.config import read_coverage_config
from coverage.exceptions import ConfigError
from coverage.misc import join_regex
from coverage.parser import PythonParser

SCOPE_PREFIXES = (".github/scripts/", ".agents/skills/")
TEST_FILE_PREFIX = ".github/scripts/test_"
SUBPROCESS_EVIDENCE = {
    "linux": ".github/scripts/check_pr_body.py",
    "windows": ".agents/skills/background-wait/scripts/background_wait.py",
}
MAX_REPORT_BYTES = 64 * 1024 * 1024
MAX_SOURCE_BYTES = 64 * 1024 * 1024
REQUIREMENTS = Path(__file__).resolve().parent / "requirements.txt"
DECIMAL = re.compile(r"[0-9]+")


@dataclass
class Block:
    """Один блок LCOV: записи строк и ветвей со сводными счётчиками."""

    lines: dict[int, int] = field(default_factory=dict)
    statements: int | None = None
    hits: int | None = None
    branches: int = 0
    branches_hit: int = 0
    branches_found: int | None = None
    branches_covered: int | None = None


def in_scope(path: str) -> bool:
    """Файл относится к измеряемой области и не является тестом."""
    if not path.startswith(SCOPE_PREFIXES) or path.startswith(TEST_FILE_PREFIX):
        return False
    return "\\" not in path and ".." not in path.split("/")


def check_path(path: str, number: int) -> str | None:
    """Проверить путь ``SF``: относительный POSIX внутри области, без ``..``."""
    if not path:
        return f"Строка {number}: пустой путь в SF"
    if "\\" in path:
        return f"Строка {number}: обратный слэш в SF ({path}); отчёт не нормализован"
    if path.startswith("/") or (len(path) > 1 and path[1] == ":"):
        return f"Строка {number}: абсолютный путь в SF ({path})"
    if ".." in path.split("/"):
        return f"Строка {number}: выход за пределы репозитория в SF ({path})"
    if not path.startswith(SCOPE_PREFIXES):
        return f"Строка {number}: файл вне измеряемой области ({path})"
    return None


def parse_lcov(text: str) -> tuple[dict[str, Block], list[str]]:
    """Разобрать записи и сводные счётчики строк и ветвей."""
    errors: list[str] = []
    blocks: dict[str, Block] = {}
    current: Block | None = None
    current_path = ""
    for number, line in enumerate(text.split("\n"), start=1):
        if not line:
            continue
        kind, _, payload = line.partition(":")
        if kind == "TN":
            continue
        if kind == "SF":
            if current is not None:
                errors.append(
                    f"Строка {number}: блок {current_path} не завершён end_of_record"
                )
            problem = check_path(payload, number)
            if problem is not None:
                errors.append(problem)
            current = Block()
            current_path = payload
            if payload in blocks:
                errors.append(f"Строка {number}: повторный блок для {payload}")
            else:
                blocks[payload] = current
            continue
        if kind == "end_of_record":
            if current is None:
                errors.append(f"Строка {number}: end_of_record без SF")
            current = None
            current_path = ""
            continue
        if current is None:
            errors.append(f"Строка {number}: запись {kind} вне блока SF")
            continue
        if kind == "DA":
            fields = payload.split(",")
            if len(fields) < 2 or not all(
                DECIMAL.fullmatch(field) for field in fields[:2]
            ):
                errors.append(f"Строка {number}: некорректная запись DA ({payload})")
                continue
            current.lines[int(fields[0])] = int(fields[1])
        elif kind in ("LF", "LH", "BRF", "BRH"):
            if not DECIMAL.fullmatch(payload):
                errors.append(
                    f"Строка {number}: некорректное значение {kind} ({payload})"
                )
                continue
            if kind == "LF":
                current.statements = int(payload)
            elif kind == "LH":
                current.hits = int(payload)
            elif kind == "BRF":
                current.branches_found = int(payload)
            else:
                current.branches_covered = int(payload)
        elif kind == "BRDA":
            fields = payload.split(",", 2)
            description, separator, taken = fields[-1].rpartition(",")
            if (
                len(fields) != 3
                or not all(DECIMAL.fullmatch(field) for field in fields[:2])
                or int(fields[0]) == 0
                or not separator
                or not description.strip()
                or (taken != "-" and not DECIMAL.fullmatch(taken))
            ):
                errors.append(f"Строка {number}: некорректная запись BRDA ({payload})")
                continue
            current.branches += 1
            current.branches_hit += taken != "-" and int(taken) > 0
    if current is not None:
        errors.append(f"Блок {current_path} не завершён end_of_record (отчёт обрезан)")
    return blocks, errors


def git_paths(root: Path, *arguments: str) -> set[str]:
    """Получить пути из git: индекс и новые файлы без промежуточного staging."""
    result = subprocess.run(  # noqa: S603 - фиксированный вызов git без shell
        ["git", "-C", str(root), "ls-files", "-z", *arguments],  # noqa: S607
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        message = result.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(message or "git ls-files завершился с ошибкой")
    return {
        item
        for item in result.stdout.decode("utf-8", "replace").split("\0")
        if item and item.endswith(".py")
    }


def expected_sources(root: Path) -> set[str]:
    """Ожидаемый набор: индекс плюс новые неигнорируемые файлы, без тестов."""
    listed = git_paths(root) | git_paths(root, "--others", "--exclude-standard")
    return {path for path in listed if in_scope(path)}


def main_block_lines(source: str, path: str) -> set[int]:
    """Строки тела ``if __name__ == "__main__":`` без самой проверки условия."""
    module = ast.parse(source, filename=path)
    lines: set[int] = set()
    for node in module.body:
        if not isinstance(node, ast.If) or not is_main_guard(node):
            continue
        for statement in node.body:
            for child in ast.walk(statement):
                lineno = getattr(child, "lineno", None)
                if isinstance(lineno, int):
                    lines.add(lineno)
    return lines


def is_main_guard(node: ast.If) -> bool:
    """Проверить, что условие блока сравнивает ``__name__`` с ``"__main__"``."""
    test = node.test
    if not isinstance(test, ast.Compare) or len(test.ops) != 1:
        return False
    if not isinstance(test.ops[0], ast.Eq) or len(test.comparators) != 1:
        return False
    left, right = test.left, test.comparators[0]
    return (is_name(left, "__name__") and is_main_value(right)) or (
        is_name(right, "__name__") and is_main_value(left)
    )


def is_name(node: ast.expr, name: str) -> bool:
    """Совпадает ли узел с именем."""
    return isinstance(node, ast.Name) and node.id == name


def is_main_value(node: ast.expr) -> bool:
    """Совпадает ли узел со строкой ``"__main__"``."""
    return isinstance(node, ast.Constant) and node.value == "__main__"


def check_evidence(root: Path, path: str, blocks: dict[str, Block]) -> list[str]:
    """Доказать, что код выполнялся дочерним процессом, а не только импортом."""
    source_file = root / path
    if not source_file.is_file():
        return [f"Нет файла для доказательства subprocess: {path}"]
    if path not in blocks:
        return [f"Файл доказательства subprocess отсутствует в отчёте: {path}"]
    lines = main_block_lines(source_file.read_text(encoding="utf-8"), path)
    if not lines:
        return [f'В {path} не найден блок if __name__ == "__main__"']
    covered = {line for line, hits in blocks[path].lines.items() if hits > 0}
    if not lines & covered:
        return [
            f"Нет покрытой строки в блоке __main__ файла {path}: дочерний процесс не атрибутирован"
        ]
    return []


def statement_lines(path: str, text: str, exclude: str = "") -> set[int]:
    """Вернуть исполняемые строки исходника достоверным парсером coverage.

    Отчёт формирует сам coverage, поэтому набор ``DA`` обязан совпадать с его
    множеством операторов. Грубый подсчёт по ``ast`` не используется: он даёт
    ложные отказы на корректных отчётах.
    """
    parser = PythonParser(text=text, filename=path, exclude=exclude)
    parser.parse_source()
    return set(parser.statements)


def report_exclusions(root: Path) -> str:
    """Прочитать настройки отчёта измеренной ревизии без загрузки плагинов."""
    config_file = root / ".coveragerc"
    config = read_coverage_config(
        str(config_file) if config_file.exists() else False,
        warn=lambda message: print(f"::warning::{message}", file=sys.stderr),
    )
    if config.plugins:
        raise ValueError(
            "плагины coverage в конфигурации измеренной ревизии не поддерживаются"
        )
    return join_regex(config.exclude_list)


def coverage_pin() -> str | None:
    """Вернуть пин ``coverage==…`` из requirements.txt того же checkout."""
    if not REQUIREMENTS.is_file():
        return None
    for line in REQUIREMENTS.read_text(encoding="utf-8").splitlines():
        if line.startswith("coverage=="):
            return line.strip()
    return None


def coverage_version_errors() -> list[str]:
    """Сверить импортируемый coverage с пином, который породил отчёт.

    Исполняемые строки вычисляет парсер того же инструмента, поэтому версия
    обязана совпадать с генератором: расхождение диагностируется как ошибка, а
    не «исправляется» молча.
    """
    pin = coverage_pin()
    if pin is None:
        return ["В requirements.txt отсутствует пин coverage: версия не доказана"]
    expected = pin.split("==", 1)[1].strip()
    actual = getattr(coverage, "__version__", "")
    if actual != expected:
        return [
            (
                f"Версия coverage {actual!r} не совпадает с пином {expected!r}:"
                " исполняемые строки могут быть посчитаны другим инструментом"
            )
        ]
    return []


def check_source_lines(
    root: Path,
    path: str,
    block: Block,
    max_bytes: int = MAX_SOURCE_BYTES,
    *,
    exclude: str = "",
) -> list[str]:
    """Сверить DA с исполняемыми строками исходника и отвергнуть подделку."""
    source = root / path
    if not source.is_file():
        return [f"Нет исходника для проверки строк: {path}"]
    try:
        resolved = source.resolve(strict=True)
        base = root.resolve()
    except OSError as error:
        return [f"Исходник недоступен: {path} ({error})"]
    if not resolved.is_relative_to(base):
        return [f"Исходник вне корня репозитория: {path}"]
    size = source.stat().st_size
    if size > max_bytes:
        return [f"Исходник {path} превышает предел {max_bytes} байт ({size})"]
    try:
        statements = statement_lines(path, source.read_text(encoding="utf-8"), exclude)
    except (OSError, UnicodeDecodeError, SyntaxError, ValueError, re.error) as error:
        return [f"Не удалось определить исполняемые строки {path}: {error}"]
    if not statements and block.lines:
        return [f"В {path} нет исполняемых строк, но отчёт содержит записи DA"]
    reported = set(block.lines)
    errors: list[str] = []
    invented = sorted(reported - statements)
    if invented:
        errors.append(
            f"{path}: попадания на неисполняемых строках: "
            + ", ".join(str(number) for number in invented[:5])
        )
    hidden = sorted(statements - reported)
    if hidden:
        errors.append(
            f"{path}: скрыты исполняемые строки отчёта: "
            + ", ".join(str(number) for number in hidden[:5])
        )
    return errors


def check_block(path: str, block: Block) -> list[str]:
    """Сверить счётчики блока с фактическими записями DA и BRDA."""
    errors: list[str] = []
    covered = covered_lines(block)
    for label, reported, actual, required, description in (
        ("LF", block.statements, len(block.lines), bool(block.lines), "числом DA"),
        ("LH", block.hits, covered, bool(block.lines), "покрытыми DA"),
        (
            "BRF",
            block.branches_found,
            block.branches,
            block.branches > 0,
            "числом BRDA",
        ),
        (
            "BRH",
            block.branches_covered,
            block.branches_hit,
            block.branches > 0,
            "покрытыми BRDA",
        ),
    ):
        if reported is None and required:
            errors.append(f"{path}: нет записи {label}")
        elif reported is not None and reported != actual:
            errors.append(
                f"{path}: {label}={reported} не совпадает с {description}={actual}"
            )
    return errors


def check_data_dir(root: Path, data_dir: Path, blocks: dict[str, Block]) -> list[str]:
    """Проверить, что данные собраны этим прогоном и не устарели."""
    if not data_dir.is_dir():
        return [f"Каталог данных отсутствует: {data_dir}"]
    data_file = data_dir / ".coverage"
    if not data_file.is_file():
        return [f"Объединённый файл данных отсутствует: {data_file}"]
    sources = [root / path for path in blocks if (root / path).is_file()]
    if not sources:
        return []
    newest = max(source.stat().st_mtime for source in sources)
    if data_file.stat().st_mtime < newest:
        return [f"Данные покрытия старше исходников: {data_file} < {newest}"]
    return []


def validate_report(
    report_path: Path,
    root: Path,
    platform: str,
    expected: set[str],
    data_dir: Path | None = None,
) -> list[str]:
    """Проверить отчёт целиком и вернуть список ошибок."""
    if not report_path.is_file():
        return [f"Отчёт отсутствует: {report_path}"]
    raw = report_path.read_bytes()
    if not raw:
        return [f"Отчёт пуст: {report_path}"]
    if len(raw) > MAX_REPORT_BYTES:
        return [f"Отчёт превышает допустимый размер: {len(raw)} байт"]
    if b"\r" in raw:
        return [
            "Отчёт не нормализован: обнаружен возврат каретки; выполните нормализацию LCOV перед валидацией"
        ]
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as error:
        return [f"Отчёт не является UTF-8: {error}"]
    blocks, errors = parse_lcov(text)
    if not blocks:
        errors.append("В отчёте нет ни одного блока SF: это не LCOV покрытия")
        return errors
    present = set(blocks)
    missing = sorted(expected - present)
    extra = sorted(present - expected)
    if missing:
        errors.append("В отчёте отсутствуют ожидаемые файлы: " + ", ".join(missing))
    if extra:
        errors.append("В отчёте есть файлы вне ожидаемого набора: " + ", ".join(extra))
    try:
        exclude = report_exclusions(root)
    except (OSError, ValueError, ConfigError) as error:
        errors.append(f"Не удалось прочитать исключения coverage: {error}")
        return errors
    for path in sorted(present & expected):
        errors.extend(check_block(path, blocks[path]))
        errors.extend(check_source_lines(root, path, blocks[path], exclude=exclude))
    if not any(block.branches for block in blocks.values()):
        errors.append("В отчёте нет записей BRDA: ветви не измерялись")
    if sum(covered_lines(block) for block in blocks.values()) == 0:
        errors.append("В отчёте нет ни одной покрытой строки")
    errors.extend(check_evidence(root, SUBPROCESS_EVIDENCE[platform], blocks))
    if data_dir is not None:
        errors.extend(check_data_dir(root, data_dir, blocks))
    return errors


def covered_lines(block: Block) -> int:
    """Сколько строк блока покрыто хотя бы один раз."""
    return sum(1 for hits in block.lines.values() if hits > 0)


def summarize(blocks: dict[str, Block]) -> str:
    """Краткая сводка для лога и артефактов."""
    covered = sum(covered_lines(block) for block in blocks.values())
    branches = sum(block.branches for block in blocks.values())
    return f"LCOV: файлов {len(blocks)}, покрытых строк {covered}, записей ветвей {branches}"


def main(argv: list[str] | None = None) -> int:
    """Разобрать аргументы, проверить отчёт и напечатать диагностику."""
    parser = argparse.ArgumentParser(
        description="Проверить LCOV runtime-покрытия Python и атрибуцию subprocess"
    )
    _ = parser.add_argument("--report", required=True)
    _ = parser.add_argument("--platform", required=True, choices=("linux", "windows"))
    _ = parser.add_argument("--repo-root", default=".")
    _ = parser.add_argument("--data-dir", default=None)
    settings = parser.parse_args(argv)
    report = Path(cast(str, settings.report)).resolve()
    root = Path(cast(str, settings.repo_root)).resolve()
    platform = cast(str, settings.platform)
    data_dir_text = cast(str | None, settings.data_dir)
    data_dir = Path(data_dir_text) if data_dir_text else None
    errors: list[str] = coverage_version_errors()
    expected: set[str] = set()
    try:
        expected = expected_sources(root)
    except (RuntimeError, FileNotFoundError) as error:
        errors.append(f"Не удалось получить ожидаемый набор из git: {error}")
    if not expected and not errors:
        errors.append("Ожидаемый набор файлов пуст")
    errors.extend(
        f"Ожидаемый файл отсутствует на диске: {path}"
        for path in sorted(expected)
        if not (root / path).is_file()
    )
    if not errors:
        errors.extend(validate_report(report, root, platform, expected, data_dir))
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    blocks, _ = parse_lcov(report.read_text(encoding="utf-8"))
    print(summarize(blocks))
    print(f"Проверен отчёт {report} для платформы {platform}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
