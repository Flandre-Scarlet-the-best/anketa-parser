#!/usr/bin/env python3
"""Проверка метаданных Agent Skills и локальных ссылок с безопасным разбором YAML."""

import argparse
import re
import sys
from pathlib import Path

import yaml

NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
LINK = re.compile(r"\]\(([^)]+)\)")
ALLOWED = {"name", "description", "license", "compatibility", "metadata", "allowed-tools"}


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
    errors.extend(validate_links(path, path.parent, body))
    return errors


def validate_links(path: Path, boundary: Path, source: str | None = None) -> list[str]:
    """Проверить локальные назначения ссылок в пределах владельца документа."""
    errors = []
    try:
        text = source if source is not None else path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"{path}: не удалось прочитать: {error}"]
    for match in LINK.finditer(text):
        target = match.group(1).split("#", 1)[0]
        if not target or target.startswith("#") or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
            continue
        resolved = (path.parent / target).resolve()
        if not resolved.is_relative_to(boundary.resolve()) or not resolved.exists():
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
