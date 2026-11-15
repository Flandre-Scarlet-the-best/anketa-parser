#!/usr/bin/env python3
"""Проверка контракта описания ручного PR репозитория (только стандартная библиотека)."""

import argparse
import json
import re
import sys
from pathlib import Path

REQUIRED = (
    "Цель",
    "Область изменений",
    "Реализация",
    "Проверки",
    "Производительность",
    "CI",
    "Готовность",
    "Ограничения",
)
BOT_BRANCHES = {"renovate[bot]": "renovate/", "dependabot[bot]": "dependabot/"}
# ATX-заголовок: закрывающие `#` — часть оформления, а не имени раздела.
HEADING = re.compile(r"^ {0,3}(#{2,3})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$")
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
# Закрывающая граница не допускает info string: ```` ```python ```` — это строка
# содержимого блока, а не его конец.
FENCE_CLOSE = re.compile(r"^ {0,3}(`{3,}|~{3,})[ \t]*$")
# Raw HTML block по CommonMark: переводы строк внутри него не создают ни
# заголовков, ни ссылок, поэтому сырой HTML нужно исключить из разбора.
HTML_RAW = re.compile(r"^<(pre|script|style|textarea)(?=[\s/>])", re.IGNORECASE)
HTML_BLOCK = re.compile(
    r"^</?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|"
    r"details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|"
    r"h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|nav|noframes|ol|"
    r"optgroup|option|p|param|section|source|summary|table|tbody|td|tfoot|th|thead|title|tr|"
    r"track|ul)(?=[\s/>]|$)",
    re.IGNORECASE,
)

PERFORMANCE = "Производительность"
STATUSES = ("измерено", "не применимо", "не измерено")
# Строка «Поле: значение», в том числе пунктом списка и с полужирным именем.
FIELD = re.compile(r"^\s*(?:[-*+]\s+)?(?:\*\*)?(?P<name>[^:|*\n]+?)(?:\*\*)?:(?:\*\*)?(?P<value>.*)$")
PLACEHOLDERS = {
    "", "-", "—", "–", "n/a", "na", "none", "нет", "todo", "tbd", "...", "…",
    "не применимо", "заполнить", "уточнить", "см. ниже", "см. выше", "?",
}
# Обязательные поля компактного решения для каждого измеренного сценария.
MEASURED_FIELDS = ("Сценарий", "База", "Кандидат", "Результат", "Вывод", "Регрессии")
MAX_BODY_LENGTH = 10_000
MAX_PERFORMANCE_LENGTH = 2_200
MAX_SIMPLE_PERFORMANCE_LENGTH = 650
NO_EVIDENCE = ("Доказательство производительности", "отсутствует")
PERFORMANCE_HINT = (
    "добавьте после ## Проверки раздел ## Производительность: компактную сводку проверенных измерений "
    "из проверенных результатов, «Статус: не применимо» с конкретной причиной или "
    "«Статус: не измерено» с причиной, диагностикой и строкой "
    "«Доказательство производительности: отсутствует»"
)


# GitHub скрывает HTML-комментарии, поэтому заголовки внутри них не являются
# видимыми разделами описания. Внутри код-блока `<!--` — литерал, а не комментарий.
def mask_list_fences(lines: list[str]) -> list[str]:
    """Скрыть fenced-блоки внутри пунктов списка до разбора комментариев.

    Отступ маркера блока считается относительно содержимого пункта; при выходе
    из списка незакрытый блок заканчивается вместе с контейнером.
    """
    result: list[str] = []
    indents: list[int] = []
    fence_char = ""
    fence_len = 0
    fence_indent = 0
    outer_char = ""
    outer_len = 0
    in_comment = False
    for line in lines:
        expanded = line.expandtabs(4)
        indent = len(expanded) - len(expanded.lstrip(" "))
        if outer_char:
            closing = FENCE_CLOSE.match(expanded)
            if closing and closing.group(1)[0] == outer_char and len(closing.group(1)) >= outer_len:
                outer_char = ""
            result.append(line)
            continue
        if fence_char:
            if expanded.strip() and indent < fence_indent:
                fence_char = ""
            else:
                closing = FENCE_CLOSE.match(expanded[fence_indent:])
                if closing and closing.group(1)[0] == fence_char and len(closing.group(1)) >= fence_len:
                    fence_char = ""
                result.append(" " * len(line))
                continue
        # Маркеры внутри комментария не открывают блоки. Сохраняем исходную
        # строку: окончательное удаление комментариев выполняет strip_comments.
        was_in_comment = in_comment
        visible = ""
        rest = expanded
        while rest:
            if in_comment:
                end = rest.find("-->")
                if end < 0:
                    break
                in_comment = False
                rest = rest[end + 3:]
            else:
                start = rest.find("<!--")
                if start < 0:
                    visible += rest
                    break
                visible += rest[:start]
                in_comment = True
                rest = rest[start + 4:]
        expanded = visible
        indent = len(expanded) - len(expanded.lstrip(" "))
        if expanded.strip():
            while indents and indent < indents[-1]:
                indents.pop()
        base = indents[-1] if indents else 0
        relative = expanded[base:]
        marker = re.match(r"^ {0,3}(?:[-*+]|\d{1,9}[.)]) +", relative)
        if marker:
            base += marker.end()
            indents.append(base)
            relative = expanded[base:]
        opening = FENCE.match(relative)
        if opening:
            token = opening.group(1)
            # У backtick-fence info string не может содержать обратную кавычку.
            if token[0] != "`" or "`" not in relative[opening.end():]:
                in_comment = was_in_comment
                if indents:
                    fence_char, fence_len, fence_indent = token[0], len(token), base
                    result.append(" " * len(line))
                    continue
                outer_char, outer_len = token[0], len(token)
        result.append(line)
    return result


def _hide(line: str) -> str:
    """Строка-заполнитель той же длины: содержимое скрыто, смещения сохранены."""
    return " " * len(line)


def _fence_open(line: str) -> tuple[str, int] | None:
    """Открывающий fenced-блок (символ, длина маркера) либо None.

    Info string backtick-блока не может содержать обратную кавычку
    (CommonMark §4.5), поэтому такая строка блок не открывает.
    """
    match = FENCE.match(line)
    if not match:
        return None
    token = match.group(1)
    if token[0] == "`" and "`" in line[match.end():]:
        return None
    return token[0], len(token)


def _fence_closes(line: str, char: str, length: int) -> bool:
    """Закрывающая граница блока: тот же символ и не меньшая длина маркера."""
    match = FENCE_CLOSE.match(line)
    return bool(match) and match.group(1)[0] == char and len(match.group(1)) >= length


def _raw_block(line: str) -> tuple[int, str] | None:
    """Тип raw HTML-блока CommonMark и его тег, либо None.

    Тип 1 (`<pre>`, `<script>`, `<style>`, `<textarea>`) длится до собственного
    закрывающего тега и может содержать пустые строки. Тип 6 (блочные теги)
    прерывается пустой строкой или концом документа. Внутри такого блока
    GitHub показывает разметку дословно, поэтому похожие на заголовки строки
    там не являются разделами.
    """
    stripped = line.lstrip(" ")
    raw = HTML_RAW.match(stripped)
    if raw:
        return 1, raw.group(1).casefold()
    if HTML_BLOCK.match(stripped):
        return 6, ""
    return None


def _raw_close(line: str, tag: str) -> int:
    """Позиция закрывающего тега того же типа, начиная с первого `<` строки."""
    start = line.find("<")
    if start < 0:
        return -1
    return line.casefold().find("</" + tag + ">", start)


def mask_raw_html(lines: list[str]) -> list[str]:
    """Скрыть содержимое raw HTML-блоков, не задевая fenced-код.

    Fenced-блок имеет приоритет над блочными тегами: пример внутри примера
    остаётся кодом, а не открытым HTML. Markdown не разбирает действительно
    отрисованный raw HTML-блок, поэтому такие строки не дают разделов.
    """
    result: list[str] = []
    block: tuple[int, str] | None = None
    fence: tuple[str, int] | None = None
    for line in lines:
        if block is not None:
            kind, tag = block
            if kind == 1:
                # Блок завершает только его собственный закрывающий тег:
                # `</style>` не закрывает `<pre>`.
                if _raw_close(line, tag) < 0:
                    result.append(_hide(line))
                    continue
                block = None
                result.append(line)
                continue
            if not line.strip():
                block = None
                result.append(line)
                continue
            result.append(_hide(line))
            continue
        if fence is not None:
            if _fence_closes(line, fence[0], fence[1]):
                fence = None
            result.append(line)
            continue
        if (opened := _fence_open(line)) is not None:
            fence = opened
            result.append(line)
            continue
        found = _raw_block(line)
        result.append(line)
        if found is not None:
            # Блок, закрывшийся на той же строке, дальше не продолжается.
            kind, tag = found
            block = None if kind == 1 and _raw_close(line, tag) >= 0 else found
    return result


def strip_comments(lines: list[str]) -> list[str]:
    """Убрать HTML-комментарии и содержимое raw HTML-блоков построчно.

    Fenced-код сохраняется как литерал: его содержимое не разбирается ни как
    HTML, ни как Markdown, поэтому `<!--` внутри примера не открывает
    комментарий и не скрывает последующие настоящие разделы.
    """
    result: list[str] = []
    inside = False
    fence: tuple[str, int] | None = None
    for line in mask_raw_html(mask_list_fences(lines)):
        if fence is not None:
            if _fence_closes(line, fence[0], fence[1]):
                fence = None
            result.append(line)
            continue
        if not inside and (opened := _fence_open(line)) is not None:
            fence = opened
            result.append(line)
            continue
        visible = ""
        rest = line
        while rest:
            if inside:
                end = rest.find("-->")
                if end < 0:
                    rest = ""
                    continue
                inside = False
                rest = rest[end + 3:]
                continue
            start = rest.find("<!--")
            if start < 0:
                visible += rest
                rest = ""
                continue
            visible += rest[:start]
            inside = True
            rest = rest[start + 4:]
        result.append(visible)
    return result


def sections(body: str, *, raw: bool = False, level: int = 2) -> list[tuple[str, str]]:
    """Видимые разделы заданного уровня: очищенное содержимое либо исходный диапазон."""
    original = body.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    lines = strip_comments(original)
    found: list[tuple[str, str]] = []
    active: list[str] = []
    fence: tuple[str, int] | None = None
    start = 0
    for index, line in enumerate(lines):
        if fence:
            if _fence_closes(line, fence[0], fence[1]):
                fence = None
            continue
        if (opened := _fence_open(line)) is not None:
            fence = opened
            continue
        match = HEADING.match(line)
        if match and len(match.group(1)) == level:
            if found:
                found[-1] = (found[-1][0], "\n".join(original[start:index]).strip() if raw else "\n".join(active))
            found.append((match.group(2).strip(), ""))
            active = []
            start = index
        elif found:
            active.append(line)
    if found:
        found[-1] = (found[-1][0], "\n".join(original[start:]).strip() if raw else "\n".join(active))
    return found


def fields(content: str) -> dict[str, list[str]]:
    """Значения строк «Имя: значение» вне таблиц по имени поля."""
    found: dict[str, list[str]] = {}
    for line in content.split("\n"):
        if line.lstrip().startswith("|"):
            continue
        match = FIELD.match(line)
        if match:
            value = match.group("value").strip().strip("*").strip()
            found.setdefault(match.group("name").strip(), []).append(value)
    return found


def placeholder(value: str) -> bool:
    text = value.strip().strip("`*_ ").casefold()
    return text in PLACEHOLDERS or re.fullmatch(r"<[^<>]*>", text) is not None


def substantive(values: list[str], minimum: int) -> bool:
    """Есть хотя бы одно конкретное пояснение, а не шаблонная заглушка."""
    return any(not placeholder(v) and len(v.strip()) >= minimum and len(v.split()) >= 3 for v in values)


def scenario_blocks(content: str) -> list[tuple[str, str]]:
    """Блоки `### …` отдельных сценариев раздела производительности по порядку."""
    return sections(content, level=3)


def validate_performance(content: str, raw_section: str | None = None) -> list[str]:
    """Проверить компактный вывод; достоверность результатов требует отдельной проверки."""
    prefix = "## Производительность: "
    errors: list[str] = []
    # Лимиты относятся ко всему разделу вместе с его заголовком.
    length = len(raw_section) if raw_section is not None else len("## Производительность\n") + len(content.strip())
    if length > MAX_PERFORMANCE_LENGTH:
        errors.append(prefix + f"{length} символов, максимум {MAX_PERFORMANCE_LENGTH}; сократите отчёт до выводов")
    found = fields(content)
    statuses = [v.strip("`").casefold() for v in found.get("Статус", [])]
    if len(statuses) != 1 or statuses[0] not in STATUSES:
        return errors + [prefix + "нужна ровно одна строка «Статус: измерено», «Статус: не применимо» или «Статус: не измерено»"]
    status = statuses[0]
    if status != "измерено" and length > MAX_SIMPLE_PERFORMANCE_LENGTH:
        errors.append(prefix + f"краткое решение занимает более {MAX_SIMPLE_PERFORMANCE_LENGTH} символов")
    if status == "не применимо":
        if not substantive(found.get("Причина", []), 40):
            errors.append(prefix + "«не применимо» требует конкретной причины не короче 40 символов")
    elif status == "не измерено":
        for name in ("Причина", "Диагностика"):
            if not substantive(found.get(name, []), 20):
                errors.append(prefix + f"«не измерено» требует фактической строки «{name}:»")
        if NO_EVIDENCE[1] not in [v.casefold() for v in found.get(NO_EVIDENCE[0], [])]:
            errors.append(prefix + "«не измерено» требует строки «Доказательство производительности: отсутствует»")
    else:
        blocks = scenario_blocks(content)
        if not blocks:
            errors.append(prefix + "каждый сценарий «измерено» описывается в отдельном `###`-подразделе")
        measured = [(heading, block) for heading, block in blocks if not heading.endswith("— не измерено")]
        for heading, block in blocks:
            if heading.endswith("— не измерено"):
                info = fields(block)
                for name in ("Причина", "Диагностика"):
                    if not info.get(name) or all(placeholder(value) for value in info[name]):
                        errors.append(prefix + f"неизмеренный сценарий требует строки «{name}:» ({heading})")
                if NO_EVIDENCE[1] not in [v.casefold() for v in info.get(NO_EVIDENCE[0], [])]:
                    errors.append(prefix + f"неизмеренный сценарий требует отсутствия доказательства ({heading})")
        if blocks and not measured:
            errors.append(prefix + "«измерено» требует хотя бы один измеренный сценарий")
        for heading, block in measured:
            where = f" ({heading})" if heading else ""
            info = fields(block)
            for name in MEASURED_FIELDS:
                values = info.get(name, [])
                if len(values) != 1 or any(placeholder(v) for v in values):
                    errors.append(prefix + f"для измерения нужна фактическая строка «{name}:»{where}")
            result = info.get("Результат", [])
            if not any(re.search(r"\d[\d.,\s]*\s*(?:ms|µs|ns|s|%|KiB|MiB|GiB|bytes|count|мс|с)\b|\d\s*%", value) for value in result):
                errors.append(prefix + f"«Результат» требует числового значения с единицей{where}")
            if re.search(r"(?m)^\s*\|\s*Метрика\s*\|", block):
                errors.append(prefix + f"не вставляйте полную таблицу отчёта: оставьте компактный вывод{where}")
    return errors

def validate(body: str) -> list[str]:
    """Детерминированный список ошибок; пустой список — структура соответствует контракту."""
    body = body.replace("\r\n", "\n").replace("\r", "\n")
    if len(body) > MAX_BODY_LENGTH:
        return [f"Описание PR: {len(body)} символов, максимум {MAX_BODY_LENGTH}"]
    blocks = sections(body)
    raw_blocks = sections(body, raw=True)
    names = [heading for heading, _ in blocks]
    errors: list[str] = []
    for name in REQUIRED:
        count = names.count(name)
        if count == 0 and name == PERFORMANCE:
            errors.append(f"Отсутствует обязательный заголовок: ## {name} — {PERFORMANCE_HINT}")
        elif count == 0:
            errors.append(f"Отсутствует обязательный заголовок: ## {name}")
        elif count > 1:
            errors.append(f"Заголовок повторяется: ## {name}")
    if all(name in names for name in REQUIRED):
        positions = [names.index(name) for name in REQUIRED]
        if positions != sorted(positions):
            errors.append("Обязательные разделы расположены не в установленном порядке")
    for index, (name, content) in enumerate(blocks):
        if name in REQUIRED and not content.strip():
            errors.append(f"Пустой обязательный раздел: ## {name}")
        if name in REQUIRED and content.strip().casefold() in {"todo", "tbd", "заполнить", "уточнить"}:
            errors.append(f"Раздел содержит только заглушку: ## {name}")
        elif name == PERFORMANCE and content.strip() and names.count(PERFORMANCE) == 1:
            errors.extend(validate_performance(content, raw_blocks[index][1]))
    return errors


def is_automation_pr(payload: dict) -> bool:
    """Узкое исключение: PR зависимостей от проверенного аккаунта бота из его префикса ветки."""
    pr = payload.get("pull_request") or {}
    login = (pr.get("user") or {}).get("login", "")
    branch = (pr.get("head") or {}).get("ref", "")
    return branch.startswith(BOT_BRANCHES.get(login, "\0"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--body-file", type=Path, help="локальный Markdown-источник описания PR до публикации")
    source.add_argument("--event-path", type=Path, help="JSON события GitHub pull_request или pull_request_target")
    args = parser.parse_args()
    try:
        if args.body_file is not None:
            body = args.body_file.read_text(encoding="utf-8")
        else:
            payload = json.loads(args.event_path.read_text(encoding="utf-8"))
            if not isinstance(payload, dict) or not isinstance(payload.get("pull_request"), dict):
                raise ValueError("Нет объекта pull_request в событии")
            if is_automation_pr(payload):
                print("PR бота обновления зависимостей: формат ручных PR не применяется")
                return 0
            body = payload["pull_request"].get("body") or ""
        problems = validate(body)
    except (OSError, UnicodeError, ValueError) as exc:
        print(f"Не удалось проверить описание PR: {exc}", file=sys.stderr)
        return 2
    if problems:
        for problem in problems:
            print(f"::error::{problem}", file=sys.stderr)
        return 1
    print("Структура описания PR соответствует контракту")
    return 0


if __name__ == "__main__":
    sys.exit(main())
