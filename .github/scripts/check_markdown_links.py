#!/usr/bin/env python3
"""Проверка локальных назначений ссылок в отслеживаемых файлах .md.

Обход Git, номера строк и границы репозитория адаптированы из
Flandre-Scarlet-the-best/anki-deck-toolkit (.github/scripts/check_markdown_links.py).
Общие правила fenced-кода и разбор назначения переиспользуются из skill checker.
Сеть, якоря, смысл документов и синтаксис Mermaid эта проверка не проверяет.
"""

import argparse
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

from check_skill_contracts import (
    AUTOLINK,
    LIST_MARKER,
    closes_link,
    fence_closes,
    fence_ends_with_container,
    fence_open,
    inline_destination,
)

# Разбор контейнеров и fenced-блоков общий; многострочные спаны и отступный код
# нужны общему Markdown, но не меняют уже действующую проверку skills.
BACKTICKS = re.compile(r"`+")
COMMENT_OPEN = "<!--"
COMMENT_CLOSE = "-->"
# Маркер цитаты перед содержимым строки; определения ссылок действуют во всём
# документе, поэтому контейнер им не мешает.
QUOTE_MARKER = re.compile(r" {0,3}>[ \t]?")
# Заголовок и тематический разрыв (включая подчёркивание setext-заголовка) сами
# завершают блок, поэтому код-спан, ссылка и определение не могут их пересекать.
HEADING = re.compile(r" {0,3}#{1,6}(?:[ \t]|$)")
BLOCK_LEAF = re.compile(
    r" {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,}|=+|-+)[ \t]*$"
)
INLINE = re.compile(r"!?\[(?:[^\[\]\n]|\n(?![ \t]*\n)|\[[^\[\]\n]*\])*\]\(")
REFERENCE = re.compile(r" {0,3}\[(?!\^)[^\]\n]+\]:[ \t]*(?:\n[ \t]*)?(<[^<>\n]*>|\S+)")


def blank_text(text: str) -> str:
    """Скрыть литерал, сохраняя смещения и номера строк."""
    return "".join("\n" if char == "\n" else " " for char in text)


def hide(text: str, start: int, end: int) -> str:
    """Скрыть диапазон литерала, сохраняя смещения и номера строк."""
    return text[:start] + blank_text(text[start:end]) + text[end:]


def mask_inline(text: str, blocks: list[tuple[int, int]]) -> str:
    """Скрыть код-спаны и HTML-комментарии внутри границ Markdown-блоков.

    Код-спан не пересекает границу блока, поэтому закрывающая граница ищется
    только внутри блока: незакрытая кавычка заголовка не соединяется с кавычкой
    следующего заголовка. Внутри блока первым обрабатывается то, что начинается
    раньше, — код-спан или комментарий, где бы он ни стоял в строке. Незакрытый
    комментарий, как и прежде, продолжается до `-->` или конца документа.
    """
    masked = text
    hidden_until = 0
    position = 0
    for start, end in blocks:
        position = max(position, start)
        while position < end:
            if position < hidden_until:
                position = min(hidden_until, end)
                continue
            comment = text.find(COMMENT_OPEN, position, end)
            opener = BACKTICKS.search(masked, position, end)
            if comment >= 0 and (opener is None or comment < opener.start()):
                closing = text.find(COMMENT_CLOSE, comment + len(COMMENT_OPEN))
                hidden_until = (
                    len(text) if closing < 0 else closing + len(COMMENT_CLOSE)
                )
                masked = hide(masked, comment, hidden_until)
                position = hidden_until
                continue
            if opener is None:
                break
            # Экранирование открывающего символа и равная длина границ кода.
            inner = opener.start() + int(escaped(masked, opener.start()))
            length = opener.end() - inner
            position = opener.end()
            if not length:
                continue
            for closer in BACKTICKS.finditer(masked, position, end):
                # Внутри кода обратная косая черта уже не экранирует границу.
                if closer.end() - closer.start() == length:
                    masked = hide(masked, inner, closer.end())
                    position = closer.end()
                    break
    return masked


def block_starts(lines: list[str]) -> list[int]:
    """Номера строк, с которых начинается новый Markdown-блок.

    Блок завершают пустая строка (в том числе строка, скрытая как код),
    заголовок, тематический разрыв, подчёркивание setext-заголовка и начало
    пункта списка: код-спан и назначение ссылки действуют только внутри блока.
    """
    starts: list[int] = []
    boundary = True
    for number, line in enumerate(lines):
        if not line.strip():
            boundary = True
            continue
        single = HEADING.match(line) is not None or BLOCK_LEAF.match(line) is not None
        if boundary or single or LIST_MARKER.match(line) is not None:
            starts.append(number)
        boundary = single
    return starts


def block_spans(lines: list[str], starts: list[int]) -> list[tuple[int, int]]:
    """Смещения блоков в тексте, склеенном переводами строк."""
    offsets: list[int] = []
    offset = 0
    for line in lines:
        offsets.append(offset)
        offset += len(line) + 1
    total = max(offset - 1, 0)
    spans: list[tuple[int, int]] = []
    for index, number in enumerate(starts):
        begin = offsets[number]
        end = offsets[starts[index + 1]] - 1 if index + 1 < len(starts) else total
        spans.append((begin, end))
    return spans


def visible_lines(text: str) -> list[str]:
    """Разметить строки: скрыть fenced-код и отступный код, не скрывая списки."""
    lines: list[str] = []
    fence: tuple[str, int, int] | None = None
    indented: int | None = None
    previous_blank = True
    containers: list[int] = []
    for raw in text.splitlines():
        line = raw.expandtabs(4)
        if fence is not None:
            if fence_ends_with_container(line, fence[2]):
                fence = None
            else:
                if fence_closes(line, *fence):
                    fence = None
                lines.append(blank_text(line))
                continue
        blank = not line.strip()
        indentation = len(line) - len(line.lstrip(" "))
        if indented is not None and (blank or indentation >= indented):
            lines.append(blank_text(line))
            continue
        # Продолжение абзаца может опускать отступ списка; после пустой строки
        # или начала нового блока уменьшение отступа уже закрывает контейнер.
        if not blank and (
            previous_blank
            or indented is not None
            or LIST_MARKER.match(line)
            or fence_open(line)
            or re.match(r" {0,3}(?:#{1,6}(?: |$)|>)", line)
        ):
            while containers and indentation < containers[-1]:
                _ = containers.pop()
        indented = None
        column = containers[-1] if containers and indentation >= containers[-1] else 0
        starts_item = False
        while marker := LIST_MARKER.match(line[column:]):
            # Более четырёх пробелов после маркера: один разделяет пункт,
            # остальные принадлежат содержимому, в том числе отступному коду.
            token = marker.group().rstrip(" ")
            padding = marker.end() - len(token)
            column += len(token) + (padding if padding <= 4 else 1)
            containers.append(column)
            starts_item = True
        if (
            not blank
            and line[column:].startswith("    ")
            and (previous_blank or starts_item)
        ):
            indented = column + 4
            lines.append(blank_text(line))
            continue
        if (opened := fence_open(line[column:])) is not None:
            fence = opened[0], opened[1], column + opened[2]
            lines.append(blank_text(line))
            previous_blank = True
            continue
        previous_blank = blank
        lines.append(line)
    return lines


def visible_document(text: str) -> tuple[str, list[tuple[int, int]]]:
    """Убрать код и комментарии, вернув видимый текст и границы блоков."""
    lines = visible_lines(text)
    spans = block_spans(lines, block_starts(lines))
    visible = mask_inline("\n".join(lines), spans)
    visible = AUTOLINK.sub(lambda match: blank_text(match.group()), visible)
    return visible, spans


def escaped(text: str, index: int) -> bool:
    """Символ экранирован нечётным числом обратных косых черт."""
    count = 0
    while index > count and text[index - count - 1] == "\\":
        count += 1
    return count % 2 == 1


def destinations(text: str) -> list[tuple[int, str]]:
    """Назначения ссылок и изображений с номерами строк записи адреса.

    Определения проверяются независимо от использования; ссылки по метке
    разрешаются внутри своего документа и используют тот же адрес определения.
    """
    visible, spans = visible_document(text)
    found: list[tuple[int, str]] = []
    for start, end in spans:
        for match in INLINE.finditer(visible, start, end):
            bracket = match.start() + match.group().index("[")
            if escaped(visible, bracket):
                continue
            position = match.end() - 1
            parsed = inline_destination(visible, position)
            if parsed is None or not closes_link(visible, parsed[1]):
                continue
            address_start = position + 1
            while visible[address_start : address_start + 1] in (" ", "\t", "\n"):
                address_start += 1
            number = visible.count("\n", 0, address_start) + 1
            found.append((number, parsed[0]))
        found.extend(reference_destinations(visible, start, end))
    return sorted(found)


def content_offset(text: str, position: int) -> int:
    """Смещение содержимого строки после маркеров цитаты и списка."""
    while True:
        quote = QUOTE_MARKER.match(text, position)
        if quote is not None:
            position = quote.end()
            continue
        marker = LIST_MARKER.match(text, position)
        if marker is None:
            return position
        position = marker.end()


def reference_destinations(visible: str, start: int, end: int) -> list[tuple[int, str]]:
    """Определения ссылок, допустимые в начале блока и после предыдущего.

    Определение не может прерывать абзац, поэтому проверяются только позиции от
    начала блока, а подряд идущие определения образуют один ряд: титул внутри
    предыдущего определения ряд не разрывает, а маркеры цитаты и списка перед
    содержимым строки распознаванию не мешают.
    """
    found: list[tuple[int, str]] = []
    position = content_offset(visible, start)
    while position < end:
        match = REFERENCE.match(visible, position, end)
        if match is None:
            break
        bracket = match.start() + match.group().index("[")
        if not escaped(visible, bracket):
            address = match.group(1).removeprefix("<").removesuffix(">")
            found.append((visible.count("\n", 0, match.start(1)) + 1, address))
        line_end = visible.find("\n", match.end(), end)
        if line_end < 0:
            break
        position = content_offset(visible, line_end + 1)
    return found


def local_target(root: Path, page: Path, address: str) -> Path | None:
    """Отбросить внешние URL, запросы и якоря; раскрыть кодирование пути."""
    parsed = urlsplit(address)
    if parsed.scheme or parsed.netloc:
        return None
    relative = unquote(parsed.path)
    relative = re.sub(r"\\([!\"#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", r"\1", relative)
    if not relative:
        return None
    if relative.startswith("/"):
        return root / relative.lstrip("/")
    return page.parent / relative


def check_page(root: Path, page: Path) -> list[str]:
    """Проверить существование и границу локальных назначений одной страницы."""
    name = page.relative_to(root).as_posix()
    try:
        text = page.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"{name}: не удалось прочитать Markdown: {error}"]
    errors: list[str] = []
    resolved_root = root.resolve()
    for number, address in destinations(text):
        try:
            target = local_target(root, page, address)
            if target is None:
                continue
            resolved = target.resolve()
            if not resolved.is_relative_to(resolved_root):
                errors.append(
                    f"{name}:{number}: ссылка ведёт за пределы репозитория: {address}"
                )
            elif not resolved.exists():
                errors.append(f"{name}:{number}: цель ссылки не существует: {address}")
        except OSError, ValueError, RuntimeError:
            errors.append(f"{name}:{number}: недопустимый путь ссылки: {address}")
    return errors


def tracked_markdown(root: Path) -> list[Path]:
    """Взять Markdown из индекса Git, включая ещё не закоммиченные добавления."""
    # Корень и список аргументов передаются без shell; содержимое Markdown
    # никогда не становится командой. Ошибка Git обрабатывается в main.
    output = subprocess.run(  # noqa: S603
        ["git", "-C", str(root), "ls-files", "-z", "--", "*.md"],  # noqa: S607
        check=True,
        capture_output=True,
    ).stdout
    return sorted(root / name for name in output.decode("utf-8").split("\0") if name)


def check_repository(root: Path) -> list[str]:
    """Проверить весь отслеживаемый Markdown, включая отсутствующие файлы."""
    return [
        error for page in tracked_markdown(root) for error in check_page(root, page)
    ]


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.root: Path = Path(__file__).resolve().parents[2]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parents[2]
    )
    args = parser.parse_args(namespace=Arguments())
    try:
        errors = check_repository(args.root.resolve())
    except OSError, UnicodeError, subprocess.CalledProcessError:
        print("Не удалось получить отслеживаемый Markdown из Git", file=sys.stderr)
        return 1
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        print(f"Ошибок локальных ссылок Markdown: {len(errors)}", file=sys.stderr)
        return 1
    print("Локальные ссылки отслеживаемого Markdown ведут в существующие места")
    return 0


if __name__ == "__main__":
    sys.exit(main())
