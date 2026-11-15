#!/usr/bin/env python3
"""Проверка метаданных Agent Skills и локальных ссылок с безопасным разбором YAML."""

import argparse
import re
import sys
from pathlib import Path
from urllib.parse import unquote

import yaml

NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
ALLOWED = {"name", "description", "license", "compatibility", "metadata", "allowed-tools"}
# Литеральные контексты, где ссылка — пример, а не назначение документа.
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
# Закрывающая граница не допускает info string: ```` ```python ```` — содержимое.
FENCE_CLOSE = re.compile(r"^ {0,3}(`{3,}|~{3,})[ \t]*$")
LABEL = re.compile(r"^ {0,3}\[([^\]]+)\]:[ \t]*(.*)$")
# Автоссылка — `<scheme:...>` или e-mail с доменом; `<path/file.md>` ею не является.
AUTOLINK = re.compile(r"<[A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*>|<[^\s<>@/]+@[^\s<>@/]+\.[^\s<>@/]+>")
# Маркер пункта списка: fenced-блок может стоять внутри контейнера.
LIST_MARKER = re.compile(r" {0,3}(?:[-*+]|\d{1,9}[.)])[ \t]+")


class UniqueSafeLoader(yaml.SafeLoader):
    """Безопасный загрузчик, запрещающий повторные ключи на любой глубине."""

    def construct_mapping(self, node, deep=False):
        seen = set()
        for key_node, _ in node.value:
            key = self.construct_object(key_node, deep=deep)
            try:
                if key in seen:
                    raise yaml.constructor.ConstructorError(
                        None, None, "повторяющийся YAML-ключ", key_node.start_mark)
                seen.add(key)
            except TypeError as error:
                raise yaml.constructor.ConstructorError(
                    None, None, "неподдерживаемый YAML-ключ", key_node.start_mark) from error
        return super().construct_mapping(node, deep=deep)


def validate_skill(path: Path) -> list[str]:
    errors: list[str] = []
    try:
        source = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"{path}: не удалось прочитать: {error}"]
    lines = source.splitlines()
    if not lines or lines[0] != "---":
        return [f"{path}: отсутствует начальный YAML frontmatter"]
    try:
        end = lines.index("---", 1)
    except ValueError:
        return [f"{path}: отсутствует закрывающий ---"]
    try:
        entries = yaml.load("\n".join(lines[1:end]), Loader=UniqueSafeLoader)
    except yaml.YAMLError as error:
        mark = getattr(error, "problem_mark", None)
        where = f", строка {mark.line + 2}" if mark else ""
        return [f"{path}: неразбираемое значение YAML frontmatter{where}"]
    if not isinstance(entries, dict) or not all(isinstance(key, str) for key in entries):
        return [f"{path}: frontmatter должен быть отображением со строковыми ключами"]
    for key in entries:
        if key not in ALLOWED:
            errors.append(f"{path}: нестандартное поле {key}; условия активации помещай в description")
    name = entries.get("name")
    if not isinstance(name, str) or not NAME.fullmatch(name) or len(name) > 64 or name != path.parent.name:
        errors.append(f"{path}: name должен совпадать с каталогом и отвечать Agent Skills spec")
    description = entries.get("description")
    if not isinstance(description, str) or not description.strip() or len(description) > 1024:
        errors.append(f"{path}: description должен быть строкой из 1–1024 символов")
    if "metadata" in entries:
        metadata = entries["metadata"]
        if not isinstance(metadata, dict) or not all(
            isinstance(key, str) and isinstance(value, str) for key, value in metadata.items()
        ):
            errors.append(f"{path}: metadata должен быть отображением строк в строки")
    for key in ("license", "compatibility", "allowed-tools"):
        if key in entries and not isinstance(entries[key], str):
            errors.append(f"{path}: {key} должен быть строкой")
    compatibility = entries.get("compatibility")
    if isinstance(compatibility, str) and len(compatibility) > 500:
        errors.append(f"{path}: compatibility должен содержать не более 500 символов")
    body = "\n".join(lines[end + 1:])
    # Ссылочные определения действуют в пределах своего документа, поэтому
    # валидатор берёт их из того же текста, в котором ищет ссылки.
    errors.extend(validate_links(path, path.parent, body))
    return errors


def fence_open(line: str) -> tuple[str, int, int] | None:
    """Открывающий fenced-блок: символ, длина маркера и колонка содержимого.

    Маркер может стоять после маркеров списка, поэтому проверяется остаток
    строки, а не только её начало.
    """
    offset = 0
    while marker := LIST_MARKER.match(line[offset:]):
        offset += marker.end()
    match = FENCE.match(line[offset:])
    if not match:
        return None
    token = match.group(1)
    # Info string backtick-блока не может содержать обратную кавычку.
    if token[0] == "`" and "`" in line[offset + match.end():]:
        return None
    return token[0], len(token), offset


def fence_closes(line: str, char: str, length: int, column: int) -> bool:
    """Закрывающая граница блока с учётом отступа контейнера списка."""
    candidates = (line, line[column:] if len(line) > column else "")
    for candidate in candidates:
        match = FENCE_CLOSE.match(candidate)
        if match and match.group(1)[0] == char and len(match.group(1)) >= length:
            return True
    return False


def mask_literals(markdown: str) -> list[str]:
    """Заменить литеральные контексты пробелами, сохранив длину строк.

    Fenced-код (в том числе внутри пунктов списка), код-спан, HTML-комментарий
    и автоссылка не содержат назначений документа, поэтому их содержимое не
    должно проверяться как ссылка.
    """
    result: list[str] = []
    stop = ""
    fence: tuple[str, int, int] | None = None
    for raw in markdown.split("\n"):
        line = raw.expandtabs(4)
        if fence is not None:
            if fence_closes(line, fence[0], fence[1], fence[2]):
                fence = None
            result.append(" " * len(line))
            continue
        if stop:
            position = line.find(stop)
            if position < 0:
                result.append(" " * len(line))
                continue
            tail = position + len(stop)
            line = " " * tail + line[tail:]
            stop = ""
        elif (opened := fence_open(line)) is not None:
            fence = opened
            result.append(" " * len(line))
            continue
        visible: list[str] = []
        index = 0
        while index < len(line):
            char = line[index]
            if char == "\\":
                visible.append(" " * min(2, len(line) - index))
                index += 2
                continue
            if char == "<":
                autolink = AUTOLINK.match(line, index)
                if autolink:
                    visible.append(" " * len(autolink.group(0)))
                    index = autolink.end()
                    continue
                if line.startswith("<!--", index):
                    end = line.find("-->", index + 4)
                    if end < 0:
                        stop = "-->"
                        visible.append(" " * (len(line) - index))
                        index = len(line)
                    else:
                        visible.append(" " * (end + 3 - index))
                        index = end + 3
                    continue
            if char == "`":
                run = len(line) - len(line[index:].lstrip("`"))
                closing = line.find("`" * run, index + run)
                if closing >= 0:
                    visible.append(" " * (closing + run - index))
                    index = closing + run
                    continue
                visible.append(line[index:index + run])
                index += run
                continue
            visible.append(char)
            index += 1
        normalized = "".join(visible)
        if len(normalized) < len(line):
            normalized += " " * (len(line) - len(normalized))
        result.append(normalized)
    return result


def inline_destination(text: str, position: int) -> tuple[str, int] | None:
    """Разобрать назначение после `(`; None, если скобка не закрывает ссылку."""
    index = position + 1
    while index < len(text) and text[index] in " \t\n":
        index += 1
    if index >= len(text):
        return None
    if text[index] == "<":
        end = index + 1
        while end < len(text) and text[end] not in "\n<>":
            end += 2 if text[end] == "\\" else 1
        if end >= len(text) or text[end] != ">":
            return None
        return text[index + 1:end], end + 1
    depth = 0
    end = index
    while end < len(text):
        char = text[end]
        if char == "\\" and end + 1 < len(text):
            end += 2
            continue
        if char == "(":
            depth += 1
        elif char == ")":
            if depth == 0:
                break
            depth -= 1
        elif char in " \t\n":
            break
        end += 1
    if depth or end == index:
        return None
    return text[index:end], end


def closes_link(text: str, position: int) -> bool:
    """Проверить закрывающую `)` после необязательного title.

    Для назначения в угловых скобках `position` указывает на `>`.
    """
    index = position
    if index < len(text) and text[index] == ">":
        index += 1
    while index < len(text) and text[index] in " \t\n":
        index += 1
    if index < len(text) and text[index] in "\"'(":
        closing = ")" if text[index] == "(" else text[index]
        index = text.find(closing, index + 1)
        if index < 0:
            return False
        index += 1
        while index < len(text) and text[index] in " \t\n":
            index += 1
    return index < len(text) and text[index] == ")"


def definition_destination(remainder: str) -> str | None:
    """Назначение из остатка строки определения `[label]: <dest> "title"`."""
    text = remainder.lstrip()
    if not text:
        return None
    if text.startswith("<"):
        end = text.find(">")
        return text[1:end] if end > 0 else None
    destination = text.split(None, 1)[0]
    return destination or None


def reference_definitions(markdown: str) -> dict[str, str]:
    """Определения ссылок по нормализованному ярлыку в нижнем регистре."""
    found: dict[str, str] = {}
    for line in mask_literals(markdown):
        match = LABEL.match(line)
        if not match:
            continue
        label = re.sub(r"\s+", " ", match.group(1)).casefold()
        destination = definition_destination(match.group(2))
        if label and destination is not None and label not in found:
            found[label] = destination
    return found


def label_key(text: str) -> str:
    """Нормализованный вид ярлыка: регистр не важен, пробелы схлопываются."""
    return re.sub(r"\s+", " ", text).casefold()


def link_targets(markdown: str, definitions: dict[str, str]) -> list[str]:
    """Назначения ссылок поддерживаемых форм Markdown вне литеральных контекстов."""
    targets: list[str] = []
    for line in mask_literals(markdown):
        # Строка `[label]: цель` — определение, а не использование ссылки.
        if LABEL.match(line):
            continue
        index = 0
        while index < len(line):
            opening = line.find("[", index)
            if opening < 0:
                break
            closing = line.find("]", opening + 1)
            if closing < 0:
                break
            index = closing + 1
            following = line[index:index + 1]
            if following == "(":
                parsed = inline_destination(line, index)
                if parsed is not None and closes_link(line, parsed[1]):
                    targets.append(parsed[0])
                continue
            if following == "[":
                end = line.find("]", index + 1)
                if end < 0:
                    continue
                label = line[index + 1:end]
                index = end + 1
            else:
                label = ""
            # Пустой ярлык означает collapsed-форму `[text][]`.
            key = label_key(label or line[opening + 1:closing])
            if key in definitions:
                targets.append(definitions[key])
    return targets


def local_target(destination: str) -> str | None:
    """Локальная цель без якоря: внешние адреса и чистые якоря не проверяются."""
    target = destination.split("#", 1)[0].strip()
    if not target or target.startswith("#") or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
        return None
    return unquote(target)


def validate_links(path: Path, boundary: Path, source: str | None = None) -> list[str]:
    """Проверить локальные назначения ссылок в пределах владельца документа.

    Определения собираются из этого же документа: в CommonMark они не
    переходят в другие файлы.
    """
    try:
        markdown = source if source is not None else path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"{path}: не удалось прочитать: {error}"]
    known = reference_definitions(markdown)
    errors: list[str] = []
    root = boundary.resolve()
    for destination in link_targets(markdown, known):
        target = local_target(destination)
        if target is None:
            continue
        try:
            resolved = (path.parent / target).resolve()
            outside = not resolved.is_relative_to(root)
            missing = not resolved.exists()
        except (OSError, ValueError):
            outside, missing = True, True
        if outside or missing:
            errors.append(f"{path}: несуществующая или выходящая за владельца ссылка: {target}")
    return errors


def validate_tree(root: Path) -> list[str]:
    base = root / ".agents" / "skills"
    directories = sorted((path for path in base.glob("*") if path.is_dir()), key=lambda path: path.name)
    if not directories:
        return ["Нет каталогов skills в .agents/skills/"]
    # Каталог skill без манифеста — тоже нарушение контракта: обход по
    # `*/SKILL.md` такую поломку не заметил бы.
    missing = [f"{directory}: отсутствует SKILL.md" for directory in directories if not (directory / "SKILL.md").is_file()]
    # Каждый документ проверяется со своими определениями: общий словарь давал
    # бы переток между файлами и терял определения самого SKILL.md.
    errors = missing + [error for skill in sorted(base.glob("*/SKILL.md")) for error in validate_skill(skill)]
    for directory in directories:
        for reference in sorted(directory.rglob("*.md")):
            if reference != directory / "SKILL.md":
                errors.extend(validate_links(reference, directory))
    if (root / "AGENTS.md").is_file():
        errors.extend(validate_links(root / "AGENTS.md", root))
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    errors = validate_tree(args.root)
    if errors:
        for error in errors:
            print(f"::error::{error}", file=sys.stderr)
        return 1
    print("Метаданные и ссылки Agent Skills соответствуют контракту")
    return 0


if __name__ == "__main__":
    sys.exit(main())
