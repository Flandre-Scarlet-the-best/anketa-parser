#!/usr/bin/env python3
"""Проверка локальных назначений ссылок во всём отслеживаемом Markdown.

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
CODE_SPAN = re.compile(r"(?<!`)(`+)(?!`)(?:(?!\n[ \t]*\n).)*?(?<!`)\1(?!`)", re.DOTALL)
COMMENT = re.compile(r"<!--.*?(?:-->|\Z)", re.DOTALL)
INLINE = re.compile(r"!?\[(?:[^\[\]\n]|\n(?![ \t]*\n)|\[[^\[\]\n]*\])*\]\(")
REFERENCE = re.compile(
    r"^ {0,3}\[[^\]\n]+\]:[ \t]*(?:\n[ \t]*)?(<[^<>\n]*>|\S+)", re.MULTILINE
)


def blank_text(text: str) -> str:
    """Скрыть литерал, сохраняя смещения и номера строк."""
    return "".join("\n" if char == "\n" else " " for char in text)


def visible_text(text: str) -> str:
    """Убрать код и комментарии, не скрывая обычные продолжения списков."""
    lines: list[str] = []
    fence: tuple[str, int, int] | None = None
    indented = False
    previous_blank = True
    in_list = False
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
        if (opened := fence_open(line)) is not None:
            fence = opened
            lines.append(blank_text(line))
            previous_blank = True
            continue
        blank = not line.strip()
        if indented and (blank or line.startswith("    ")):
            lines.append(blank_text(line))
            continue
        indented = False
        if not blank and line.startswith("    ") and previous_blank and not in_list:
            indented = True
            lines.append(blank_text(line))
            continue
        if LIST_MARKER.match(line):
            in_list = True
        elif previous_blank and line[:1] not in ("", " ", "\t"):
            in_list = False
        previous_blank = blank
        lines.append(line)
    visible = "\n".join(lines)
    visible = CODE_SPAN.sub(lambda match: blank_text(match.group()), visible)
    visible = COMMENT.sub(lambda match: blank_text(match.group()), visible)
    return AUTOLINK.sub(lambda match: blank_text(match.group()), visible)


def escaped(text: str, index: int) -> bool:
    """Скобка экранирована нечётным числом обратных косых черт."""
    count = 0
    while index > count and text[index - count - 1] == "\\":
        count += 1
    return count % 2 == 1


def destinations(text: str) -> list[tuple[int, str]]:
    """Назначения ссылок и изображений с номерами строк записи адреса.

    Определения проверяются независимо от использования; ссылки по метке
    разрешаются внутри своего документа и используют тот же адрес определения.
    """
    visible = visible_text(text)
    found: list[tuple[int, str]] = []
    for match in INLINE.finditer(visible):
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
    for match in REFERENCE.finditer(visible):
        bracket = match.start() + match.group().index("[")
        if not escaped(visible, bracket):
            address = match.group(1).removeprefix("<").removesuffix(">")
            found.append((visible.count("\n", 0, match.start(1)) + 1, address))
    return sorted(found)


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
