"""Сверить охват BasedPyright с целевыми исходниками, независимо от исключений."""

import json
import sys
from pathlib import Path
from typing import cast

from check_pr_body import JSONValue


def validate_coverage(report_text: str, config_path: Path) -> list[str]:
    """Конфигурация задаёт корни; исключения не разрешают терять их исходники.

    Поддерживаются явные каталоги include. Новая форма конфигурации требует
    явного согласования проверки, а не молчаливого изменения знаменателя.
    """
    config = cast(JSONValue, json.loads(config_path.read_text(encoding="utf-8")))
    report = cast(JSONValue, json.loads(report_text))
    if not isinstance(config, dict) or not isinstance(report, dict):
        return ["Некорректная конфигурация или отчёт BasedPyright"]
    includes = config.get("include")
    summary = report.get("summary")
    if not isinstance(includes, list) or not includes or not isinstance(summary, dict):
        return ["Отсутствуют целевые каталоги или сводка BasedPyright"]
    expected: set[Path] = set()
    for entry in includes:
        if not isinstance(entry, str) or any(c in entry for c in "*?[]"):
            return ["Проверка охвата требует явных каталогов include"]
        directory = (config_path.parent / entry).resolve()
        if not directory.is_dir():
            return ["Целевой каталог BasedPyright отсутствует"]
        expected.update(directory.rglob("*.py"))
    count = summary.get("filesAnalyzed")
    if not expected or type(count) is not int or count != len(expected):
        return [f"Неполный охват BasedPyright: {count}; ожидается {len(expected)}"]
    return []


if __name__ == "__main__":
    errors = validate_coverage(
        Path(sys.argv[1]).read_text(encoding="utf-8"),
        Path(sys.argv[2]),
    )
    for error in errors:
        print(error, file=sys.stderr)
    raise SystemExit(bool(errors))
