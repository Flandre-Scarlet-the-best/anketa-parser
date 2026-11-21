"""Привести пути Cobertura .NET к виду относительно корня репозитория.

Отчёт Microsoft.Testing.Extensions.CodeCoverage содержит абсолютные пути
(``/home/runner/work/<repo>/<repo>/src/...`` или ``C:\\...\\src\\...``) и не
содержит ``<sources>``. Абсолютный путь раннера в опубликованном отчёте
недетерминирован, поэтому шаг переводит его в ``src/...``: граница отсечения
вычисляется по сегменту ``src``, а не по временному каталогу runner.
Сомнительные пути (без ``src``, с ``..``, несуществующий файл) отвергаются.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import cast
from xml.etree import ElementTree

MAX_REPORT_BYTES = 64 * 1024 * 1024
ROOT_TAG = "coverage"
PREFIX = "src/"
PATH_ATTRIBUTES = ("filename",)


@dataclass(frozen=True)
class Counts:
    """Счётчики нормализации: доказательство работы шага в логе."""

    rewritten: int
    unchanged: int
    rejected: int

    def describe(self) -> str:
        return (
            "Cobertura нормализована:"
            f" переписано путей {self.rewritten},"
            f" уже относительных {self.unchanged},"
            f" отвергнуто {self.rejected}"
        )


def relative_source(path: str, root: Path) -> tuple[str | None, str | None]:
    """Вернуть путь ``src/...`` или диагностику, почему он неприемлем."""
    normalized = path.replace("\\", "/")
    if not normalized:
        return None, "пустой путь файла"
    if ".." in normalized.split("/"):
        return None, f"выход за пределы репозитория: {path}"
    marker = "/" + PREFIX
    if normalized.startswith(PREFIX):
        candidate = normalized
    elif marker in normalized:
        candidate = normalized[normalized.index(marker) + 1 :]
    else:
        return None, f"путь вне src: {path}"
    if not candidate.startswith(PREFIX) or candidate == PREFIX:
        return None, f"путь вне src: {path}"
    if not (root / candidate).is_file():
        return None, f"исходник не найден: {candidate}"
    return candidate, None


def normalize(text: str, root: Path) -> tuple[bytes | None, Counts, list[str]]:
    """Разобрать отчёт, переписать пути классов и вернуть новые байты."""
    if not text.strip():
        return None, Counts(0, 0, 0), ["Отчёт пуст"]
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        return None, Counts(0, 0, 0), ["Отчёт содержит DOCTYPE или ENTITY"]
    try:
        document = ElementTree.fromstring(text)  # noqa: S314
    except ElementTree.ParseError as error:
        return None, Counts(0, 0, 0), [f"Повреждённый отчёт: {error}"]
    if document.tag != ROOT_TAG:
        return (
            None,
            Counts(0, 0, 0),
            [f"Корневой элемент {document.tag!r} вместо coverage"],
        )
    if list(document.iter("source")):
        return (
            None,
            Counts(0, 0, 0),
            ["Ожидался отчёт без <sources>: относительные пути проверяются напрямую"],
        )
    classes = list(document.iter("class"))
    if not classes:
        return None, Counts(0, 0, 0), ["В отчёте нет ни одного класса"]
    errors: list[str] = []
    rewritten = 0
    unchanged = 0
    rejected = 0
    for element in classes:
        for attribute in PATH_ATTRIBUTES:
            value = element.get(attribute)
            if value is None:
                continue
            candidate, problem = relative_source(value, root)
            if candidate is None:
                errors.append(problem or "неизвестный путь")
                rejected += 1
                continue
            if candidate == value:
                unchanged += 1
            else:
                element.set(attribute, candidate)
                rewritten += 1
    counts = Counts(rewritten, unchanged, rejected)
    if errors:
        return None, counts, errors
    body = ElementTree.tostring(document, encoding="unicode")
    return body.encode("utf-8"), counts, []


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--report", required=True, help="путь к Cobertura XML")
    _ = parser.add_argument(
        "--repo-root", default=".", help="корень репозитория для проверки путей"
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
    """Нормализовать отчёт; сомнительные пути — диагностируемая ошибка."""
    settings = build_parser().parse_args(argv)
    report = Path(cast(str, settings.report))
    root = Path(cast(str, settings.repo_root)).resolve()
    if not report.is_file():
        print(f"::error::Отчёт отсутствует: {report}", file=sys.stderr)
        return 1
    raw = report.read_bytes()
    if len(raw) > cast(int, settings.max_bytes):
        print(
            f"::error::Отчёт превышает допустимый размер: {len(raw)} байт",
            file=sys.stderr,
        )
        return 1
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as error:
        print(f"::error::Отчёт не является UTF-8: {error}", file=sys.stderr)
        return 1
    normalized, counts, errors = normalize(text, root)
    if normalized is None:
        for error in errors:
            print(f"::error::Cobertura не нормализована: {error}", file=sys.stderr)
        return 1
    output_setting = cast(str | None, settings.output)
    output = report if output_setting is None else Path(output_setting)
    output.parent.mkdir(parents=True, exist_ok=True)
    _ = output.write_bytes(normalized)
    print(counts.describe())
    print(f"Нормализован отчёт {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
