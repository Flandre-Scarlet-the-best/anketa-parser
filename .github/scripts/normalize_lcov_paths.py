"""Нормализовать LCOV-отчёт покрытия Python перед валидацией и загрузкой.

coverage.py на Windows пишет CRLF и обратные слэши в путях ``SF:``. Валидатор
такой отчёт намеренно отвергает, поэтому нормализация вынесена в отдельный шаг:
пропущенный шаг виден как ошибка проверки, а не маскируется «исправлением»
внутри валидатора. Переписывается только payload строк ``SF:`` и окончания
строк; сомнительные пути (абсолютные, с ``..``, пустые, вне измеряемой области)
отвергаются, а не переписываются.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from check_python_coverage_report import check_path, expected_sources

MAX_REPORT_BYTES = 64 * 1024 * 1024
SF_PREFIX = "SF:"
CRLF = "\r\n"


@dataclass(frozen=True)
class Counts:
    """Счётчики нормализации: доказательство работы шага, а не только код возврата."""

    separators: int
    line_endings: int
    files: int
    rejected: int

    def describe(self) -> str:
        return (
            "LCOV нормализован:"
            f" разделителей {self.separators},"
            f" окончаний строк {self.line_endings},"
            f" файлов {self.files},"
            f" отвергнутых путей {self.rejected}"
        )


def normalize(text: str, expected: set[str]) -> tuple[str, Counts, list[str]]:
    """Переписать разделители и окончания строк, проверив каждый путь ``SF:``."""
    if not text:
        return "", Counts(0, 0, 0, 0), ["Отчёт пуст: нет ни одной строки"]
    line_endings = text.count(CRLF)
    unified = text.replace(CRLF, "\n")
    output: list[str] = []
    errors: list[str] = []
    separators = 0
    files = 0
    rejected = 0
    for number, line in enumerate(unified.split("\n"), start=1):
        if not line.startswith(SF_PREFIX):
            # Остальные записи (DA, BRDA, LF, LH, end_of_record) не трогаются:
            # payload BRDA содержит произвольный текст, включая запятые и слэши.
            output.append(line)
            continue
        payload = line[len(SF_PREFIX) :]
        if not payload:
            errors.append(f"Строка {number}: пустой путь в SF")
            rejected += 1
            continue
        posix = payload.replace("\\", "/")
        if posix != payload:
            separators += 1
        problem = check_path(posix, number)
        if problem is not None:
            errors.append(problem)
            rejected += 1
            continue
        if posix not in expected:
            errors.append(f"Строка {number}: файл отсутствует в git: {posix}")
            rejected += 1
            continue
        files += 1
        output.append(SF_PREFIX + posix)
    result = "\n".join(output)
    if "\r" in result:
        errors.append(
            "В отчёте остались возвраты каретки: окончания строк не нормализованы"
        )
    if files == 0 and not errors:
        errors.append("В отчёте нет ни одной строки SF: это не LCOV покрытия")
    return result, Counts(separators, line_endings, files, rejected), errors


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--report", required=True, help="путь к LCOV-отчёту")
    _ = parser.add_argument(
        "--repo-root", default=".", help="корень репозитория для git-набора"
    )
    _ = parser.add_argument(
        "--output", default=None, help="куда записать отчёт (по умолчанию на месте)"
    )
    _ = parser.add_argument(
        "--max-bytes",
        type=int,
        default=MAX_REPORT_BYTES,
        help="предельный размер входного отчёта",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    """Нормализовать отчёт; на любом сомнительном пути вернуть ошибку."""
    settings = build_parser().parse_args(argv)
    report = Path(cast(str, settings.report))
    root = Path(cast(str, settings.repo_root)).resolve()
    if not report.is_file():
        print(f"::error::Отчёт отсутствует: {report}", file=sys.stderr)
        return 1
    raw = report.read_bytes()
    limit = cast(int, settings.max_bytes)
    if len(raw) > limit:
        print(
            f"::error::Отчёт превышает допустимый размер: {len(raw)} байт",
            file=sys.stderr,
        )
        return 1
    if b"\x00" in raw:
        print("::error::Отчёт содержит двоичные данные", file=sys.stderr)
        return 1
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as error:
        print(f"::error::Отчёт не является UTF-8: {error}", file=sys.stderr)
        return 1
    try:
        expected = expected_sources(root)
    except (RuntimeError, FileNotFoundError) as error:
        print(
            f"::error::Не удалось получить ожидаемый набор из git: {error}",
            file=sys.stderr,
        )
        return 1
    if not expected:
        print("::error::Ожидаемый набор файлов пуст", file=sys.stderr)
        return 1
    normalized, counts, errors = normalize(text, expected)
    if errors:
        for error in errors:
            print(f"::error::LCOV не нормализован: {error}", file=sys.stderr)
        return 1
    output_setting = cast(str | None, settings.output)
    output = report if output_setting is None else Path(output_setting)
    output.parent.mkdir(parents=True, exist_ok=True)
    _ = output.write_bytes(normalized.encode("utf-8"))
    print(counts.describe())
    print(f"Нормализован отчёт {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
