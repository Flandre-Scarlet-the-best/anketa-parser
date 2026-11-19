#!/usr/bin/env python3
"""Проверка секретов закреплённым Gitleaks в новом диапазоне Git и аудит истории.

Проверка разрешает диапазон коммитов по событию GitHub, требует полной истории и
отсутствия недостающих объектов, запускает Gitleaks с полной редакцией значений и
публикует только безопасные метаданные находок. Значения секретов, совпавшие
фрагменты и строки содержимого не выводятся ни при успехе, ни при ошибке.
"""

import argparse
import json
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import TypeGuard, cast

# Пользовательский код находки отделяет найденные секреты от ошибок инструмента.
LEAK_EXIT_CODE = 10
CLEAN_EXIT_CODE = 0
# Коды выхода проверки: находки блокируют, нарушение охвата или сбой инструмента
# тоже блокируют, но описываются отдельной причиной.
FINDINGS_CODE = 1
ERROR_CODE = 2
SHA = re.compile(r"[0-9a-f]{40}(?:[0-9a-f]{24})?")
ZERO_SHA = re.compile(r"0{40}|0{64}")
# Метаданные находки, разрешённые к публикации. Secret, Match, Line, Message и
# авторские поля не публикуются.
SAFE_FIELDS = ("RuleID", "File", "StartLine", "Commit", "Fingerprint")
# Диагностика инструмента ограничена по длине и уже редактирована самим Gitleaks.
MAX_DIAGNOSTIC = 2000
HISTORY_MODE = "history"
RANGE_MODE = "range"
EMPTY_MODE = "empty"
AUDIT_MODE = "audit"


class CoverageError(Exception):
    """Охват проверки не доказан: диапазон, история или объекты неполны."""


@dataclass(frozen=True)
class Scan:
    """Разрешённый по событию предмет проверки."""

    mode: str
    base: str = ""
    head: str = ""
    description: str = ""
    # Перезапись истории push может удалить старый tip: тогда допустим полный
    # аудит достижимой истории нового tip с явной отметкой замены диапазона.
    history_fallback: bool = False

    def fallback(self) -> Scan | None:
        """Полный аудит истории нового tip, если диапазон недоступен."""
        if not self.history_fallback or not self.head:
            return None
        return Scan(
            HISTORY_MODE,
            head=self.head,
            description=f"вся история до {self.head}",
            history_fallback=True,
        )

    def log_opts(self) -> str:
        """Аргументы git log для Gitleaks: диффы слияний и текстовые диффы.

        `--text` обязателен: атрибут `-diff` в `.gitattributes` помечает файл как
        двоичный, и без него Gitleaks пропускает содержимое такого файла.
        """
        if self.mode == RANGE_MODE:
            return f"--diff-merges=on --text {self.base}..{self.head}"
        if self.mode == HISTORY_MODE:
            return f"--diff-merges=on --text {self.head}"
        return "--diff-merges=on --text --all"

    def revision(self) -> str:
        """Ревизия для подсчёта коммитов и полноты объектов."""
        if self.mode == RANGE_MODE:
            return f"{self.base}..{self.head}"
        if self.mode == HISTORY_MODE:
            return self.head
        return "--all"


def payload_from(path: Path) -> dict[str, object]:
    """Прочитать объект события GitHub."""
    try:
        data = cast(object, json.loads(path.read_text(encoding="utf-8")))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise CoverageError(f"Не удалось прочитать событие: {error}") from error
    if not isinstance(data, dict):
        raise CoverageError("Событие не является JSON-объектом")
    return cast(dict[str, object], data)


def is_mapping(value: object) -> TypeGuard[dict[str, object]]:
    """Отображение строк в значения события либо иное значение."""
    return isinstance(value, dict)


def nested(payload: dict[str, object], *keys: str) -> object:
    """Значение вложенного поля события либо None."""
    current: object = payload
    for key in keys:
        if not is_mapping(current):
            return None
        current = current.get(key)
    return current


def commit_sha(payload: dict[str, object], *keys: str) -> str:
    """Обязательный SHA события; нулевой или некорректный SHA не принимается."""
    value = nested(payload, *keys)
    if (
        not isinstance(value, str)
        or not SHA.fullmatch(value)
        or ZERO_SHA.fullmatch(value)
    ):
        raise CoverageError(f"Отсутствует корректный SHA в поле {'.'.join(keys)}")
    return value


def resolve_scan(event_name: str, payload: dict[str, object]) -> Scan:
    """Разрешить проверяемый диапазон по событию; неизвестное событие — ошибка."""
    if event_name == "pull_request":
        base = commit_sha(payload, "pull_request", "base", "sha")
        head = commit_sha(payload, "pull_request", "head", "sha")
        return Scan(RANGE_MODE, base, head, f"коммиты PR {base}..{head}")
    if event_name == "merge_group":
        base = commit_sha(payload, "merge_group", "base_sha")
        head = commit_sha(payload, "merge_group", "head_sha")
        return Scan(RANGE_MODE, base, head, f"коммиты группы слияния {base}..{head}")
    if event_name == "push":
        before = nested(payload, "before")
        after = nested(payload, "after")
        if not isinstance(before, str) or not isinstance(after, str):
            raise CoverageError("Событие push не содержит before и after")
        if not ZERO_SHA.fullmatch(after):
            if not SHA.fullmatch(after):
                raise CoverageError("Событие push содержит некорректный after")
            if ZERO_SHA.fullmatch(before):
                return Scan(
                    HISTORY_MODE,
                    head=after,
                    description=f"вся история нового ref до {after}",
                )
            if not SHA.fullmatch(before):
                raise CoverageError("Событие push содержит некорректный before")
            return Scan(
                RANGE_MODE,
                before,
                after,
                f"коммиты push {before}..{after}",
                history_fallback=True,
            )
        if not ZERO_SHA.fullmatch(before):
            return Scan(EMPTY_MODE, description="ref удалён: нового дерева нет")
        raise CoverageError("Событие push не содержит проверяемого диапазона")
    raise CoverageError(f"Событие {event_name} не поддерживается проверкой секретов")


def run_git(repo: Path, *args: str) -> subprocess.CompletedProcess[str]:
    """Запустить Git без shell; аргументы — список, а не строка команды."""
    return subprocess.run(  # noqa: S603
        ["git", "-C", str(repo), *args],  # noqa: S607
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )


def git_text(repo: Path, *args: str) -> str:
    """Вывод Git или ошибка охвата; сообщение не содержит данных секретов."""
    result = run_git(repo, *args)
    if result.returncode != 0:
        raise CoverageError(
            f"Git не выполнил {args[0]}: код {result.returncode} {result.stderr.strip()[:MAX_DIAGNOSTIC]}"
        )
    return result.stdout


def require_full_history(repo: Path) -> None:
    """Неглубокий клон не доказывает полный охват истории."""
    if git_text(repo, "rev-parse", "--is-shallow-repository").strip() != "false":
        raise CoverageError(
            "Клон неглубокий: полная история нужна для доказанного охвата (fetch-depth: 0)"
        )


def require_commit(repo: Path, sha: str) -> None:
    """Объект коммита должен существовать до запуска сканера."""
    result = run_git(repo, "cat-file", "-e", f"{sha}^{{commit}}")
    if result.returncode != 0:
        raise CoverageError(f"Коммит {sha} отсутствует в клоне: диапазон неполон")


def require_complete_objects(repo: Path, revision: str) -> int:
    """Все объекты диапазона доступны, а коммитов больше нуля.

    `rev-list --missing=print` отмечает отсутствующие объекты строкой с `?`:
    такой диапазон нельзя называть проверенным, даже если сканер завершился.
    """
    missing = [
        line
        for line in git_text(
            repo, "rev-list", "--objects", "--missing=print", revision
        ).split("\n")
        if line.startswith("?")
    ]
    if missing:
        raise CoverageError(
            f"В диапазоне отсутствуют объекты Git ({len(missing)}): охват неполон"
        )
    count = git_text(repo, "rev-list", "--count", revision).strip()
    if not count.isdigit():
        raise CoverageError(f"Не удалось подсчитать коммиты диапазона: {count}")
    return int(count)


def verify_scan(repo: Path, scan: Scan) -> int:
    """Доказать охват: полная история, наличие концов и объектов диапазона."""
    require_full_history(repo)
    for sha in (scan.base, scan.head):
        if sha:
            require_commit(repo, sha)
    return require_complete_objects(repo, scan.revision())


def project(finding: object) -> dict[str, str] | None:
    """Оставить только безопасные метаданные находки."""
    if not isinstance(finding, dict):
        return None
    item = cast(dict[str, object], finding)
    projected = {
        name: str(value)
        for name in SAFE_FIELDS
        if isinstance((value := item.get(name)), str | int)
    }
    if "RuleID" not in projected or "File" not in projected:
        return None
    return projected


def dedupe(findings: list[dict[str, str]]) -> list[dict[str, str]]:
    """Диффы слияний дают повтор одной находки; ключ — отпечаток либо поля."""
    unique: dict[str, dict[str, str]] = {}
    for finding in findings:
        key = finding.get("Fingerprint") or "|".join(
            finding.get(name, "") for name in SAFE_FIELDS
        )
        _ = unique.setdefault(key, finding)
    return list(unique.values())


def read_report(path: Path) -> list[dict[str, str]]:
    """Прочитать отчёт Gitleaks и оставить безопасные поля."""
    if not path.exists():
        raise CoverageError(
            "Gitleaks не создал отчёт: результат нельзя считать проверенным"
        )
    try:
        data = cast(object, json.loads(path.read_text(encoding="utf-8")))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise CoverageError(f"Отчёт Gitleaks повреждён: {error}") from error
    if not isinstance(data, list):
        raise CoverageError("Отчёт Gitleaks не является списком находок")
    projected = [project(item) for item in cast(list[object], data)]
    findings = [item for item in projected if item is not None]
    if len(findings) != len(cast(list[object], data)):
        raise CoverageError("Отчёт Gitleaks содержит записи неизвестного формата")
    return dedupe(findings)


def scan_git(
    repo: Path,
    binary: Path,
    scan: Scan,
    report_dir: Path,
) -> list[dict[str, str]]:
    """Запустить Gitleaks по разрешённому диапазону и проверить соответствие кодов."""
    report_dir.mkdir(parents=True, exist_ok=True)
    report = report_dir / "gitleaks-findings.json"
    if report.exists():
        _ = report.unlink()
    command = [
        str(binary),
        "git",
        "--no-banner",
        "--no-color",
        "--redact=100",
        f"--exit-code={LEAK_EXIT_CODE}",
        "--report-format",
        "json",
        "--report-path",
        str(report),
        "--log-opts",
        scan.log_opts(),
        str(repo),
    ]
    try:
        result = subprocess.run(  # noqa: S603
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
    except OSError as error:
        raise CoverageError(f"Gitleaks не запустился: {error}") from error
    if any(
        "[git]" in line and re.search(r"\b(?:ERR|error)\b", line, re.IGNORECASE)
        for line in result.stderr.splitlines()
    ):
        raise CoverageError("Gitleaks сообщил об ошибке Git; охват не доказан")
    if result.returncode not in (LEAK_EXIT_CODE, CLEAN_EXIT_CODE):
        # При сбое инструмента отчёт может отсутствовать, поэтому код выхода
        # проверяется до чтения файла: иначе причина отказа теряется.
        # Текст stderr не публикуется: ошибка конфигурации может содержать
        # литерал секрета из правила без `id`, и редакция `--redact` его не скроет.
        raise CoverageError(
            f"Gitleaks завершился с кодом {result.returncode}; охват не доказан"
        )
    findings = read_report(report)
    if result.returncode == LEAK_EXIT_CODE:
        if not findings:
            raise CoverageError("Gitleaks сообщил о находках без записей в отчёте")
        return findings
    if findings:
        raise CoverageError("Gitleaks вернул отчёт о находках при успешном коде")
    return []


def describe(findings: list[dict[str, str]]) -> list[str]:
    """Строки отчёта: только правило, путь, строка и коммит."""
    lines: list[str] = []
    for finding in findings:
        place = f"{finding.get('File', '?')}:{finding.get('StartLine', '?')}"
        commit = finding.get("Commit", "")
        suffix = f", коммит {commit}" if commit else ""
        lines.append(f"правило={finding.get('RuleID', '?')}, место={place}{suffix}")
    return lines


class Arguments(argparse.Namespace):
    def __init__(self) -> None:
        super().__init__()
        self.event_name: str = ""
        self.event_path: Path | None = None
        self.repo: Path = Path(".")
        self.binary: Path = Path("gitleaks")
        self.report_dir: Path | None = None
        self.audit: bool = False


def build_parser() -> argparse.ArgumentParser:
    """Аргументы проверки: событие GitHub либо однократный аудит всех refs."""
    parser = argparse.ArgumentParser(description=__doc__)
    _ = parser.add_argument("--event-name", default="")
    _ = parser.add_argument("--event-path", type=Path)
    _ = parser.add_argument("--repo", type=Path, default=Path("."))
    _ = parser.add_argument("--binary", type=Path, default=Path("gitleaks"))
    _ = parser.add_argument("--report-dir", type=Path)
    _ = parser.add_argument(
        "--audit",
        action="store_true",
        help="проверить все доступные refs вместо диапазона события",
    )
    return parser


def resolve(args: Arguments) -> Scan:
    """Предмет проверки: событие GitHub либо аудит всех доступных refs."""
    if args.audit:
        return Scan(AUDIT_MODE, description="все доступные refs полной истории")
    if not args.event_name:
        raise CoverageError("Не задано событие GitHub и не выбран режим аудита")
    if args.event_path is None:
        raise CoverageError("Не задан путь к событию GitHub")
    return resolve_scan(args.event_name, payload_from(args.event_path))


def resolved_scan(repo: Path, scan: Scan) -> tuple[Scan, int]:
    """Доказать охват диапазона; для push допустим явный переход к истории tip."""
    try:
        return scan, verify_scan(repo, scan)
    except CoverageError as error:
        fallback = scan.fallback()
        if fallback is None:
            raise
        notice = f"::warning::Диапазон {scan.description} недоступен ({error})"
        print(
            f"{notice}; проверяется {fallback.description}",
            file=sys.stderr,
        )
        return fallback, verify_scan(repo, fallback)


def main() -> int:
    args = build_parser().parse_args(namespace=Arguments())
    try:
        scan = resolve(args)
        if scan.mode == EMPTY_MODE:
            print(f"Gitleaks: {scan.description}; проверять нечего")
            return CLEAN_EXIT_CODE
        repo = args.repo.resolve()
        scan, commits = resolved_scan(repo, scan)
        report_dir = args.report_dir or Path(tempfile.mkdtemp(prefix="gitleaks-"))
        findings = scan_git(repo, args.binary, scan, report_dir)
    except CoverageError as error:
        print(f"::error::Проверка секретов не доказана: {error}", file=sys.stderr)
        return ERROR_CODE
    print(f"Gitleaks: {scan.description}; коммитов в проверке: {commits}")
    if not findings:
        print("Gitleaks: находок нет")
        return CLEAN_EXIT_CODE
    for line in describe(findings):
        print(f"::error::Обнаружен секрет: {line}", file=sys.stderr)
    total = f"Gitleaks: находок {len(findings)}; значения секретов не публикуются."
    print(
        f"{total} Требуется отзыв или ротация секрета владельцем.",
        file=sys.stderr,
    )
    return FINDINGS_CODE


if __name__ == "__main__":
    sys.exit(main())
