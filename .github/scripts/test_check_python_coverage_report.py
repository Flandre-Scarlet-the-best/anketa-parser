"""Проверки валидатора LCOV покрытия Python: атрибуция, набор файлов, ошибки данных."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from typing import override

VALIDATOR = Path(__file__).with_name("check_python_coverage_report.py")
SCRIPTS = ".github/scripts/"
SKILL_SCRIPT = ".agents/skills/background-wait/scripts/background_wait.py"
MAIN_SOURCE = """\
def main() -> int:
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
"""
HELPER_SOURCE = """\
def helper() -> int:
    return 1
"""


def block(path: str, lines: dict[int, int], branches: int = 1) -> str:
    """Собрать корректный блок LCOV для файла."""
    rows = [f"SF:{path}"]
    rows.extend(f"DA:{number},{hits}" for number, hits in sorted(lines.items()))
    rows.append(f"LF:{len(lines)}")
    rows.append(f"LH:{sum(1 for hits in lines.values() if hits > 0)}")
    if branches:
        rows.extend(
            (
                "BRDA:1,0,jump to line 2,1",
                f"BRF:{branches}",
                f"BRH:{branches}",
            )
        )
    rows.append("end_of_record")
    return "\n".join(rows)


class ReportCase(unittest.TestCase):
    """Общая фикстура: временный git-репозиторий с исходниками и отчётом."""

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temp: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()
        self.report: Path = Path()

    @override
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.write(SCRIPTS + "check_pr_body.py", MAIN_SOURCE)
        self.write(SCRIPTS + "helper.py", HELPER_SOURCE)
        self.write(SCRIPTS + "test_ignored.py", "value = 1\n")
        self.write(SKILL_SCRIPT, MAIN_SOURCE)
        self.git("init", "-q")
        self.git("add", "--all")
        self.report = self.root / "coverage.lcov"
        self.write_report(self.default_report())

    def write(self, name: str, text: str) -> None:
        """Создать файл фикстуры вместе с каталогами."""
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")

    def git(self, *arguments: str) -> None:
        """Выполнить git в фикстуре без вывода."""
        result = subprocess.run(  # noqa: S603 - фиксированный вызов в фикстуре
            ["git", "-C", str(self.root), *arguments],  # noqa: S607
            capture_output=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", "replace"))

    def default_report(self) -> str:
        """Отчёт, проходящий все проверки linux-профиля."""
        return "\n".join(
            (
                block(SCRIPTS + "helper.py", {1: 1, 2: 1}),
                block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 5: 1, 6: 1}),
                block(SKILL_SCRIPT, {1: 1, 2: 1, 5: 1, 6: 1}),
                "",
            )
        )

    def write_report(self, text: str, newline: str = "\n") -> None:
        """Записать отчёт в кодировке UTF-8 с заданным переводом строк."""
        _ = self.report.write_bytes(text.replace("\n", newline).encode("utf-8"))

    def run_validator(
        self, *arguments: str, platform: str = "linux"
    ) -> subprocess.CompletedProcess[str]:
        """Запустить отслеживаемый файл валидатора как отдельный процесс."""
        return subprocess.run(  # noqa: S603 - запуск проверяемого локального скрипта
            [
                sys.executable,
                str(VALIDATOR),
                "--report",
                str(self.report),
                "--platform",
                platform,
                "--repo-root",
                str(self.root),
                *arguments,
            ],
            cwd=self.root,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

    def assert_error(self, result: subprocess.CompletedProcess[str], text: str) -> None:
        """Проверить код возврата и наличие диагностики."""
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn(text, result.stderr)


class ValidReportTests(ReportCase):
    def test_valid_report_passes_with_summary(self):
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("LCOV: файлов 3", result.stdout)

    def test_valid_zero_percent_file_is_allowed(self):
        self.write_report(
            "\n".join(
                (
                    block(SCRIPTS + "helper.py", {1: 0, 2: 0}),
                    block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 5: 1, 6: 1}),
                    block(SKILL_SCRIPT, {1: 1, 2: 1, 5: 1, 6: 1}),
                    "",
                )
            )
        )
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_untracked_new_file_joins_expected_set_without_staging(self):
        self.write(SCRIPTS + "extra.py", "def run() -> int:\n    return 2\n")
        expected_block = block(SCRIPTS + "extra.py", {1: 1, 2: 1})
        self.write_report(self.default_report() + expected_block + "\n")
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_test_modules_are_excluded_from_expected_set(self):
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("test_ignored.py", result.stdout)

    def test_windows_platform_accepts_window_only_evidence(self):
        result = self.run_validator(platform="windows")
        self.assertEqual(result.returncode, 0, result.stderr)


class BrokenDataTests(ReportCase):
    def test_missing_report_is_error(self):
        self.report.unlink()
        self.assert_error(self.run_validator(), "Отчёт отсутствует")

    def test_empty_report_is_error(self):
        self.write_report("")
        self.assert_error(self.run_validator(), "Отчёт пуст")

    def test_truncated_report_is_error(self):
        self.write_report(self.default_report().replace("end_of_record\n", "", 1))
        self.assert_error(self.run_validator(), "завершён")

    def test_non_lcov_text_is_error(self):
        self.write_report("это не отчёт покрытия\n")
        self.assert_error(self.run_validator(), "нет ни одного блока SF")

    def test_crlf_report_requires_normalization(self):
        self.write_report(self.default_report(), newline="\r\n")
        self.assert_error(self.run_validator(), "не нормализован")

    def test_duplicate_block_is_error(self):
        self.write_report(self.default_report() + block(SCRIPTS + "helper.py", {1: 1}))
        self.assert_error(self.run_validator(), "повторный блок")

    def test_negative_hits_are_rejected(self):
        self.write_report(self.default_report().replace("DA:2,1", "DA:2,-1", 1))
        self.assert_error(self.run_validator(), "некорректная запись DA")

    def test_negative_lf_is_rejected(self):
        self.write_report(self.default_report().replace("LF:4", "LF:три", 1))
        self.assert_error(self.run_validator(), "некорректное значение LF")


class AttributionTests(ReportCase):
    def test_absolute_path_is_error(self):
        self.write_report(
            self.default_report().replace("SF:" + SCRIPTS, "SF:/tmp/" + SCRIPTS, 1)
        )
        self.assert_error(self.run_validator(), "абсолютный путь")

    def test_parent_directory_is_error(self):
        self.write_report(
            self.default_report().replace("SF:" + SCRIPTS, "SF:../" + SCRIPTS, 1)
        )
        self.assert_error(self.run_validator(), "выход за пределы репозитория")

    def test_backslash_paths_are_error(self):
        self.write_report(
            self.default_report().replace(
                "SF:.github/scripts/helper.py", "SF:.github\\scripts\\helper.py"
            )
        )
        self.assert_error(self.run_validator(), "обратный слэш")

    def test_vendor_path_outside_scope_is_error(self):
        self.write_report(self.default_report() + block("node_modules/tool.py", {1: 1}))
        self.assert_error(self.run_validator(), "вне измеряемой области")

    def test_expected_file_missing_from_report_is_error(self):
        self.write_report(
            "\n".join(
                (
                    block(SCRIPTS + "helper.py", {1: 1, 2: 1}),
                    block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 5: 1, 6: 1}),
                    "",
                )
            )
        )
        self.assert_error(self.run_validator(), "отсутствуют ожидаемые файлы")

    def test_extra_file_in_report_is_error(self):
        # Файл в области измерения, но его нет ни в индексе, ни в рабочем дереве.
        self.write_report(self.default_report() + block(SCRIPTS + "ghost.py", {1: 1}))
        self.assert_error(self.run_validator(), "вне ожидаемого набора")

    def test_no_covered_lines_is_error(self):
        self.write_report(
            "\n".join(
                (
                    block(SCRIPTS + "helper.py", {1: 0, 2: 0}),
                    block(SCRIPTS + "check_pr_body.py", {1: 0, 2: 0, 5: 0, 6: 0}),
                    block(SKILL_SCRIPT, {1: 0, 2: 0, 5: 0, 6: 0}),
                    "",
                )
            )
        )
        self.assert_error(self.run_validator(), "нет ни одной покрытой строки")

    def test_missing_branch_data_is_error(self):
        self.write_report(
            "\n".join(
                (
                    block(SCRIPTS + "helper.py", {1: 1, 2: 1}, branches=0),
                    block(
                        SCRIPTS + "check_pr_body.py",
                        {1: 1, 2: 1, 5: 1, 6: 1},
                        branches=0,
                    ),
                    block(SKILL_SCRIPT, {1: 1, 2: 1, 5: 1, 6: 1}, branches=0),
                    "",
                )
            )
        )
        self.assert_error(self.run_validator(), "нет записей BRDA")

    def test_counter_mismatch_is_error(self):
        self.write_report(self.default_report().replace("LF:4", "LF:6", 1))
        self.assert_error(self.run_validator(), "не совпадает с числом DA")
        self.write_report(self.default_report())
        self.write_report(self.default_report().replace("LH:4", "LH:1", 1))
        self.assert_error(self.run_validator(), "не совпадает с покрытыми DA")


class SubprocessEvidenceTests(ReportCase):
    def test_linux_requires_covered_main_block(self):
        self.write_report(self.default_report().replace("DA:6,1", "DA:6,0", 1))
        self.assert_error(self.run_validator(), "блоке __main__")

    def test_windows_requires_covered_skill_main_block(self):
        self.write_report(self.default_report().replace("DA:6,1", "DA:6,0"))
        result = self.run_validator(platform="windows")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("background_wait.py", result.stderr)

    def test_missing_evidence_file_is_error(self):
        (self.root / SKILL_SCRIPT).unlink()
        result = self.run_validator(platform="windows")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("background_wait.py", result.stderr)

    def test_evidence_without_main_block_is_error(self):
        self.write(SKILL_SCRIPT, "def run() -> int:\n    return 0\n")
        self.write_report(
            "\n".join(
                (
                    block(SCRIPTS + "helper.py", {1: 1, 2: 1}),
                    block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 5: 1, 6: 1}),
                    block(SKILL_SCRIPT, {1: 1, 2: 1}),
                    "",
                )
            )
        )
        result = self.run_validator(platform="windows")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('блок if __name__ == "__main__"', result.stderr)


class DataDirTests(ReportCase):
    def test_missing_data_directory_is_error(self):
        self.assert_error(
            self.run_validator("--data-dir", str(self.root / "absent")),
            "Каталог данных отсутствует",
        )

    def test_data_dir_without_combined_file_is_error(self):
        empty = self.root / "data"
        empty.mkdir()
        self.assert_error(
            self.run_validator("--data-dir", str(empty)),
            "Объединённый файл данных отсутствует",
        )

    def test_stale_data_is_error(self):
        data = self.root / "data"
        data.mkdir()
        combined = data / ".coverage"
        _ = combined.write_bytes(b"data")
        old = time.time() - 3600
        os.utime(combined, (old, old))
        self.assert_error(
            self.run_validator("--data-dir", str(data)),
            "старше исходников",
        )

    def test_fresh_data_passes(self):
        data = self.root / "data"
        data.mkdir()
        _ = (data / ".coverage").write_bytes(b"data")
        result = self.run_validator("--data-dir", str(data))
        self.assertEqual(result.returncode, 0, result.stderr)


class RepositoryTests(ReportCase):
    def test_directory_without_git_fails_closed(self):
        other = Path(tempfile.mkdtemp(prefix="no-git-"))
        self.addCleanup(lambda: shutil.rmtree(other, ignore_errors=True))
        result = subprocess.run(  # noqa: S603 - запуск проверяемого скрипта
            [
                sys.executable,
                str(VALIDATOR),
                "--report",
                str(self.report),
                "--platform",
                "linux",
                "--repo-root",
                str(other),
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("ожидаемый набор из git", result.stderr)


class SourceLineTests(ReportCase):
    """DA сверяется с исполняемыми строками исходника, а не только с LF/LH."""

    def test_invented_line_outside_source_is_error(self):
        report = self.default_report().replace(
            block(SCRIPTS + "helper.py", {1: 1, 2: 1}),
            block(SCRIPTS + "helper.py", {999999: 1}),
        )
        self.write_report(report)
        self.assert_error(self.run_validator(), "неисполняемых строках")

    def test_hidden_executable_lines_are_error(self):
        report = self.default_report().replace(
            block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 5: 1, 6: 1}),
            block(SCRIPTS + "check_pr_body.py", {1: 1, 2: 1, 6: 1}),
        )
        self.write_report(report)
        self.assert_error(self.run_validator(), "скрыты исполняемые строки")


if __name__ == "__main__":
    _ = unittest.main()
