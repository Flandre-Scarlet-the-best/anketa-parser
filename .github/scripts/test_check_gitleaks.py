"""Регрессии проверки секретов: разрешение диапазонов и настоящий Gitleaks.

Модульные сценарии не требуют бинарника. Интеграционные создают синтетические
репозитории в временном каталоге и включаются только при GITLEAKS_BINARY: они
доказывают блокировку находки без раскрытия значения, ложноположительные
placeholder-ы, охват промежуточных коммитов и диффов слияний, а также отказ при
неглубокой истории и отсутствующих объектах.
"""

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from typing import override
from unittest.mock import patch

import check_gitleaks
from check_gitleaks import (
    CLEAN_EXIT_CODE,
    ERROR_CODE,
    FINDINGS_CODE,
    CoverageError,
    Scan,
    dedupe,
    describe,
    project,
    read_report,
    resolve_scan,
    verify_scan,
)

# Интеграционные сценарии исполняются только с установленным закреплённым Gitleaks.
BINARY = os.environ.get("GITLEAKS_BINARY", "")
DIGITS = uuid.uuid4().int


def synthetic_secret() -> str:
    """Синтетический токен известного формата, собранный во время исполнения.

    Литерал значения в репозиторий не попадает: проверка доказывает блокировку,
    не публикуя секрет и не создавая постоянного образца для правил.
    """
    alphabet = uuid.uuid4().hex
    return (
        f"xoxb-{DIGITS % 10**12:012d}-{DIGITS // 10**12 % 10**12:012d}-"
        f"{alphabet}{alphabet[:8]}"
    )


class EventTestCase(unittest.TestCase):
    """Общие помощники разрешения события."""

    def resolve(self, event_name: str, payload: dict[str, object]) -> Scan:
        return resolve_scan(event_name, payload)

    def assert_coverage_error(
        self, event_name: str, payload: dict[str, object]
    ) -> None:
        with self.assertRaises(CoverageError):
            _ = resolve_scan(event_name, payload)


class GitleaksEventTests(EventTestCase):
    def test_pull_request_uses_base_and_head_commits(self) -> None:
        base = "a" * 40
        head = "b" * 40
        scan = self.resolve(
            "pull_request",
            {"pull_request": {"base": {"sha": base}, "head": {"sha": head}}},
        )
        self.assertEqual(scan.mode, check_gitleaks.RANGE_MODE)
        self.assertEqual(scan.log_opts(), f"--diff-merges=on {base}..{head}")

    def test_merge_group_uses_group_shas(self) -> None:
        scan = self.resolve(
            "merge_group", {"merge_group": {"base_sha": "c" * 40, "head_sha": "d" * 40}}
        )
        self.assertEqual(scan.revision(), f"{'c' * 40}..{'d' * 40}")

    def test_push_scans_whole_range_and_new_ref_scans_history(self) -> None:
        range_scan = self.resolve("push", {"before": "e" * 40, "after": "f" * 40})
        self.assertEqual(range_scan.mode, check_gitleaks.RANGE_MODE)
        new_ref = self.resolve("push", {"before": "0" * 40, "after": "f" * 40})
        self.assertEqual(new_ref.mode, check_gitleaks.HISTORY_MODE)
        self.assertEqual(new_ref.log_opts(), f"--diff-merges=on {'f' * 40}")

    def test_deleted_ref_reports_absent_tree(self) -> None:
        scan = self.resolve("push", {"before": "e" * 40, "after": "0" * 40})
        self.assertEqual(scan.mode, check_gitleaks.EMPTY_MODE)
        self.assertIn("удалён", scan.description)

    def test_push_range_offers_history_fallback_only_for_push(self) -> None:
        push = self.resolve("push", {"before": "e" * 40, "after": "f" * 40})
        self.assertIsNotNone(push.fallback())
        pull = self.resolve(
            "pull_request",
            {"pull_request": {"base": {"sha": "a" * 40}, "head": {"sha": "b" * 40}}},
        )
        self.assertIsNone(pull.fallback())

    def test_unverifiable_events_and_shas_are_rejected(self) -> None:
        self.assert_coverage_error("pull_request", {"pull_request": {}})
        self.assert_coverage_error("schedule", {})
        self.assert_coverage_error("push", {"before": "e" * 40})
        self.assert_coverage_error("push", {"before": "xyz", "after": "f" * 40})
        self.assert_coverage_error("push", {"before": "e" * 40, "after": "not-a-sha"})
        self.assert_coverage_error(
            "pull_request",
            {"pull_request": {"base": {"sha": "a" * 40}, "head": {"sha": "0" * 40}}},
        )

    def test_audit_mode_covers_all_refs(self) -> None:
        scan = Scan(check_gitleaks.AUDIT_MODE, description="аудит")
        self.assertEqual(scan.log_opts(), "--diff-merges=on --all")
        self.assertEqual(scan.revision(), "--all")


class GitleaksReportTests(unittest.TestCase):
    def test_projection_keeps_only_safe_metadata(self) -> None:
        finding = {
            "RuleID": "slack-bot-token",
            "File": "config/app.env",
            "StartLine": 12,
            "Commit": "a" * 40,
            "Fingerprint": "a" * 40 + ":config/app.env:slack-bot-token:12",
            "Secret": "value-that-must-not-be-published",
            "Match": "value-that-must-not-be-published",
            "Line": "token = value-that-must-not-be-published",
            "Message": "commit message",
        }
        projected = project(finding) or {}
        self.assertEqual(projected.get("RuleID"), "slack-bot-token")
        self.assertEqual(projected.get("StartLine"), "12")
        self.assertNotIn("Secret", projected)
        self.assertEqual(
            sorted(projected),
            sorted(["RuleID", "File", "StartLine", "Commit", "Fingerprint"]),
        )
        rendered = " ".join(describe([projected]))
        self.assertNotIn("value-that-must-not-be-published", rendered)
        self.assertIn("config/app.env:12", rendered)

    def test_records_without_required_fields_are_rejected(self) -> None:
        self.assertIsNone(project("текст"))
        self.assertIsNone(project({"File": "a.txt"}))
        self.assertIsNone(project({"RuleID": "generic-api-key"}))

    def test_dedupe_keeps_one_record_per_fingerprint(self) -> None:
        item = {"RuleID": "slack-bot-token", "File": "a.txt", "Fingerprint": "fp"}
        self.assertEqual(dedupe([dict(item), dict(item)]), [item])

    def test_missing_or_invalid_report_is_coverage_error(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "report.json"
            with self.assertRaises(CoverageError):
                _ = read_report(missing)
            broken = Path(directory) / "broken.json"
            _ = broken.write_text("{", encoding="utf-8")
            with self.assertRaises(CoverageError):
                _ = read_report(broken)
            not_a_list = Path(directory) / "object.json"
            _ = not_a_list.write_text("{}", encoding="utf-8")
            with self.assertRaises(CoverageError):
                _ = read_report(not_a_list)
            unknown = Path(directory) / "unknown.json"
            _ = unknown.write_text('[{"unexpected": true}]', encoding="utf-8")
            with self.assertRaises(CoverageError):
                _ = read_report(unknown)
            empty = Path(directory) / "empty.json"
            _ = empty.write_text("[]", encoding="utf-8")
            self.assertEqual(read_report(empty), [])


class GitleaksDiagnosticTests(unittest.TestCase):
    def test_git_errors_block_clean_exit_without_exposing_stderr(self) -> None:
        scan = Scan(check_gitleaks.HISTORY_MODE, head="a" * 40)
        with tempfile.TemporaryDirectory() as directory:
            for diagnostic, fails in (
                ("ERR [git] private-value", True),
                ("WRN exhaustive rename detection was skipped", False),
                (
                    "WRN Auto packing the repository in background for optimum performance",
                    False,
                ),
            ):
                result = subprocess.CompletedProcess([], 0, "", diagnostic)
                with (
                    self.subTest(diagnostic=diagnostic),
                    patch("check_gitleaks.subprocess.run", return_value=result),
                    patch.object(check_gitleaks, "read_report", return_value=[]),
                ):
                    if fails:
                        with self.assertRaises(CoverageError) as error:
                            _ = check_gitleaks.scan_git(
                                Path(directory), Path("gitleaks"), scan, Path(directory)
                            )
                        self.assertNotIn("private-value", str(error.exception))
                    else:
                        self.assertEqual(
                            check_gitleaks.scan_git(
                                Path(directory), Path("gitleaks"), scan, Path(directory)
                            ),
                            [],
                        )


@unittest.skipUnless(BINARY, "нужен закреплённый Gitleaks: задайте GITLEAKS_BINARY")
class GitleaksRepositoryTests(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.repo: Path = Path()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name) / "repo"
        self.repo.mkdir()
        _ = self.git("init", "-b", "main")
        _ = self.git("config", "user.email", "fixture@example.invalid")
        _ = self.git("config", "user.name", "fixture")
        _ = self.git("config", "commit.gpgsign", "false")

    def git(self, *args: str) -> str:
        result = subprocess.run(  # noqa: S603
            ["git", "-C", str(self.repo), *args],  # noqa: S607
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        return result.stdout.strip()

    def write(self, name: str, text: str) -> None:
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")

    def commit(self, message: str) -> str:
        _ = self.git("add", "-A")
        _ = self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD")

    def check(self, scan: Scan) -> tuple[int, str]:
        """Полный проход проверки: охват, сканирование и безопасный отчёт."""
        output = io.StringIO()
        errors = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            try:
                commits = verify_scan(self.repo, scan)
                findings = check_gitleaks.scan_git(
                    self.repo,
                    Path(BINARY),
                    scan,
                    Path(self.temporary.name if self.temporary else ".") / "report",
                )
            except CoverageError as error:
                print(
                    f"::error::Проверка секретов не доказана: {error}", file=sys.stderr
                )
                return ERROR_CODE, errors.getvalue()
            print(f"Gitleaks: {scan.description}; коммитов в проверке: {commits}")
            for line in describe(findings):
                print(f"::error::Обнаружен секрет: {line}", file=sys.stderr)
            code = FINDINGS_CODE if findings else CLEAN_EXIT_CODE
            return code, output.getvalue() + errors.getvalue()

    def scan(self, base: str, head: str) -> Scan:
        return Scan(check_gitleaks.RANGE_MODE, base, head, f"{base}..{head}")

    def test_clean_history_and_placeholders_pass(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        self.write(
            "docs/configuration.md",
            'token = "${APP_TOKEN}"\npassword = "example"\napi_key = "<подставить>"\n',
        )
        self.write("src/app.env.example", "APP_TOKEN=${APP_TOKEN}\n")
        head = self.commit("документированные заглушки")
        code, output = self.check(self.scan(base, head))
        self.assertEqual(code, CLEAN_EXIT_CODE, output)
        self.assertNotIn("Обнаружен секрет", output)

    def test_secret_in_new_commit_blocks_without_disclosure(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        secret = synthetic_secret()
        self.write("config/app.env", f"token = {secret}\n")
        head = self.commit("добавлено значение")
        code, output = self.check(self.scan(base, head))
        self.assertEqual(code, FINDINGS_CODE, output)
        self.assertNotIn(secret, output)
        self.assertIn("slack-bot-token", output)
        self.assertIn("config/app.env", output)

    def test_invalid_git_attribute_is_coverage_error(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        self.write("config/app.env", f"token = {synthetic_secret()}\n")
        _ = self.commit("значение")
        self.write(".gitattributes", "* invalid[attr\n")
        self.write("other.txt", "данные\n")
        head = self.commit("ошибочный атрибут")
        code, output = self.check(self.scan(base, head))
        self.assertEqual(code, ERROR_CODE, output)
        self.assertIn("ошибке Git", output)
        self.assertNotIn("invalid[attr", output)

    def test_secret_added_and_removed_inside_range_is_found(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        secret = synthetic_secret()
        self.write("config/app.env", f"token = {secret}\n")
        _ = self.commit("промежуточное добавление")
        self.write("config/app.env", "token = заменить\n")
        head = self.commit("удалено из текущего дерева")
        code, output = self.check(self.scan(base, head))
        self.assertEqual(code, FINDINGS_CODE, output)
        self.assertNotIn(secret, output)

    def test_secret_only_in_merge_commit_is_found(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        _ = self.git("checkout", "-q", "-b", "side")
        self.write("side.txt", "side\n")
        _ = self.commit("боковая ветка")
        _ = self.git("checkout", "-q", "main")
        self.write("main.txt", "main\n")
        _ = self.commit("основная ветка")
        _ = self.git("merge", "-q", "--no-ff", "--no-commit", "side")
        secret = synthetic_secret()
        self.write("evil.txt", f"token = {secret}\n")
        head = self.commit("слияние")
        code, output = self.check(self.scan(base, head))
        self.assertEqual(code, FINDINGS_CODE, output)
        self.assertNotIn(secret, output)

    def test_shallow_clone_is_not_full_coverage(self) -> None:
        self.write("README.md", "# Проект\n")
        _ = self.commit("начало")
        self.write("README.md", "# Проект\n\nТекст\n")
        head = self.commit("второй")
        shallow = Path(self.temporary.name if self.temporary else ".") / "shallow"
        _ = subprocess.run(  # noqa: S603
            [  # noqa: S607
                "git",
                "clone",
                "--quiet",
                "--depth",
                "1",
                self.repo.as_uri(),
                str(shallow),
            ],
            check=True,
            capture_output=True,
        )
        with self.assertRaises(CoverageError) as context:
            _ = verify_scan(shallow, self.scan("0" * 40, head))
        self.assertIn("неглубокий", str(context.exception))

    def test_absent_commit_is_coverage_error(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        self.write("README.md", "# Проект\n\nТекст\n")
        head = self.commit("второй")
        with self.assertRaises(CoverageError) as context:
            _ = verify_scan(self.repo, self.scan(base, "9" * 40))
        self.assertIn("отсутствует", str(context.exception))
        self.assertEqual(verify_scan(self.repo, self.scan(base, head)), 1)

    def test_push_range_rewrite_falls_back_to_full_history(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        secret = synthetic_secret()
        self.write("config/app.env", f"token = {secret}\n")
        head = self.commit("значение")
        rewritten = Scan(
            check_gitleaks.RANGE_MODE,
            "9" * 40,
            head,
            "перезаписанный диапазон",
            history_fallback=True,
        )
        resolved, commits = check_gitleaks.resolved_scan(self.repo, rewritten)
        self.assertEqual(resolved.mode, check_gitleaks.HISTORY_MODE)
        self.assertEqual(commits, 2)
        with self.assertRaises(CoverageError) as context:
            _ = check_gitleaks.resolved_scan(self.repo, self.scan(base, "9" * 40))
        self.assertIn("отсутствует", str(context.exception))

    def test_failing_binary_never_passes(self) -> None:
        self.write("README.md", "# Проект\n")
        base = self.commit("начало")
        self.write("README.md", "# Проект\n\nТекст\n")
        head = self.commit("второй")
        with self.assertRaises(CoverageError):
            _ = check_gitleaks.scan_git(
                self.repo,
                Path(self.temporary.name if self.temporary else ".") / "missing-binary",
                self.scan(base, head),
                Path(self.temporary.name if self.temporary else ".") / "report",
            )


class GitleaksEventFileTests(unittest.TestCase):
    def test_event_file_requires_json_object(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "event.json"
            _ = path.write_text(json.dumps(["не объект"]), encoding="utf-8")
            with self.assertRaises(CoverageError):
                _ = check_gitleaks.payload_from(path)
            _ = path.write_text("{", encoding="utf-8")
            with self.assertRaises(CoverageError):
                _ = check_gitleaks.payload_from(path)
            with self.assertRaises(CoverageError):
                _ = check_gitleaks.payload_from(Path(directory) / "absent.json")
