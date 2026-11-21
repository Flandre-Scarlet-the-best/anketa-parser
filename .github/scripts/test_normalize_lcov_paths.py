"""Поведенческие проверки нормализатора LCOV: только SF, только разделители.

Скрипт запускается по отслеживаемому пути из репозитория, поэтому его точки
входа покрываются тестами, а не рабочим прогоном CI.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import override

from check_python_coverage_report import check_path, parse_lcov

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / ".github/scripts"
FIXTURES = SCRIPTS / "fixtures/coverage_workflow"
NORMALIZER = SCRIPTS / "normalize_lcov_paths.py"
RAW_WINDOWS = FIXTURES / "python-lcov-windows-raw.lcov"
EMPTY = FIXTURES / "python-lcov-empty.lcov"
BRDA_WITH_TEXT = "BRDA:5,0,ветка \\ альтернатива,1"


def normalizer_command(*arguments: str) -> list[str]:
    """Команда запуска нормализатора по отслеживаемому пути, без shell."""
    return [sys.executable, str(NORMALIZER), *arguments]


def run_normalizer(
    *arguments: str, env: dict[str, str] | None = None
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(  # noqa: S603 - фиксированная команда без shell
        normalizer_command(*arguments),
        cwd=ROOT,
        env={**os.environ, "PYTHONIOENCODING": "utf-8", **(env or {})},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def with_crlf(raw: bytes) -> bytes:
    """Привести окончания строк к CRLF: `.gitattributes` хранит фикстуры с LF."""
    return raw.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")


class NormalizerTests(unittest.TestCase):
    directory: Path
    report: Path

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.directory = Path()
        self.report = Path()

    @override
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.report = self.directory / "lcov.info"
        self.addCleanup(shutil.rmtree, self.directory, True)

    def copy_fixture(self, fixture: Path) -> bytes:
        """Скопировать фикстуру с CRLF, как её выпускает генератор на Windows."""
        raw = with_crlf(fixture.read_bytes())
        _ = self.report.write_bytes(raw)
        return raw

    def test_raw_windows_report_is_normalized_into_validator_input(self):
        _ = self.copy_fixture(RAW_WINDOWS)
        result = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(result.returncode, 0, result.stderr)
        raw = self.report.read_bytes()
        self.assertNotIn(b"\r", raw)
        text = raw.decode("utf-8")
        self.assertNotIn("окончаний строк 0", result.stdout)
        blocks, errors = parse_lcov(text)
        self.assertEqual(errors, [])
        for path in blocks:
            self.assertIsNone(check_path(path, 0), path)
            self.assertFalse(path.startswith("\\"))
        self.assertEqual(
            sorted(blocks),
            [
                ".agents/skills/background-wait/scripts/background_wait.py",
                ".github/scripts/check_pr_body.py",
                ".github/scripts/normalize_lcov_paths.py",
            ],
        )
        self.assertIn("разделителей 3", result.stdout)
        self.assertIn("окончаний строк", result.stdout)
        self.assertIn("файлов 3", result.stdout)
        self.assertIn(BRDA_WITH_TEXT, text)

    def test_second_run_is_byte_identical_with_zero_counters(self):
        _ = self.copy_fixture(RAW_WINDOWS)
        first = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(first.returncode, 0, first.stderr)
        normalized = self.report.read_bytes()
        second = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(self.report.read_bytes(), normalized)
        self.assertIn("разделителей 0", second.stdout)
        self.assertIn("окончаний строк 0", second.stdout)
        self.assertIn("отвергнутых путей 0", second.stdout)

    def test_absolute_paths_are_rejected_not_rewritten(self):
        payloads = (
            "C:\\repo\\.github\\scripts\\check_pr_body.py",
            "/home/runner/work/anketa-parser/anketa-parser/.github/scripts/check_pr_body.py",
        )
        for payload in payloads:
            with self.subTest(payload=payload):
                original = f"SF:{payload}\r\nDA:1,1\r\nend_of_record\r\n".encode()
                _ = self.report.write_bytes(original)
                result = run_normalizer(
                    "--report", str(self.report), "--repo-root", str(ROOT)
                )
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn("абсолютный путь", result.stderr)
                self.assertEqual(self.report.read_bytes(), original)

    def test_parent_traversal_empty_payload_and_missing_sf_are_rejected(self):
        cases = (
            ("SF:../.github/scripts/check_pr_body.py\nend_of_record\n", "пределы"),
            ("SF:\nend_of_record\n", "пустой путь"),
            ("DA:1,1\nend_of_record\n", "нет ни одной строки SF"),
            ("", "Отчёт пуст"),
        )
        for content, message in cases:
            with self.subTest(content=content, message=message):
                _ = self.report.write_text(content, encoding="utf-8")
                result = run_normalizer(
                    "--report", str(self.report), "--repo-root", str(ROOT)
                )
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn(message, result.stderr)

    def test_out_of_scope_and_unknown_files_are_rejected(self):
        cases = (
            (
                "SF:src/AnketaParser.Core/ExitCodes.cs\nend_of_record\n",
                "вне измеряемой",
            ),
            (
                "SF:.github/scripts/check_absent.py\nend_of_record\n",
                "отсутствует в git",
            ),
        )
        for content, message in cases:
            with self.subTest(content=content):
                _ = self.report.write_text(content, encoding="utf-8")
                result = run_normalizer(
                    "--report", str(self.report), "--repo-root", str(ROOT)
                )
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn(message, result.stderr)

    def test_non_positive_size_limit_is_rejected_before_reading(self):
        _ = self.copy_fixture(RAW_WINDOWS)
        result = run_normalizer(
            "--report", str(self.report), "--repo-root", str(ROOT), "--max-bytes", "0"
        )
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn("предел размера должен быть больше нуля", result.stderr)

    def test_diagnostics_are_capped_for_a_long_report(self):
        body = "\n".join(
            f"SF:.github/scripts/absent{index}.py\nend_of_record" for index in range(25)
        )
        _ = self.report.write_text(body, encoding="utf-8")
        result = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(result.returncode, 1)
        self.assertIn("диагностических сообщений не показано", result.stderr)
        self.assertEqual(result.stderr.count("файл отсутствует в git"), 20)

    def test_success_leaves_no_temporary_files(self):
        _ = self.copy_fixture(RAW_WINDOWS)
        result = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            [path.name for path in self.directory.iterdir()], [self.report.name]
        )

    def test_binary_oversized_and_missing_reports_are_rejected(self):
        _ = self.report.write_bytes(b"SF:\x00\nend_of_record\n")
        binary = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(binary.returncode, 1)
        self.assertIn("двоичные данные", binary.stderr)
        _ = self.copy_fixture(RAW_WINDOWS)
        oversized = run_normalizer(
            "--report", str(self.report), "--repo-root", str(ROOT), "--max-bytes", "16"
        )
        self.assertEqual(oversized.returncode, 1)
        self.assertIn("превышает допустимый размер", oversized.stderr)
        absent = run_normalizer(
            "--report", str(self.directory / "absent.info"), "--repo-root", str(ROOT)
        )
        self.assertEqual(absent.returncode, 1)
        self.assertIn("Отчёт отсутствует", absent.stderr)

    def test_output_file_keeps_order_encoding_and_single_trailing_newline(self):
        target = self.directory / "normalized.info"
        _ = self.copy_fixture(RAW_WINDOWS)
        result = run_normalizer(
            "--report",
            str(self.report),
            "--repo-root",
            str(ROOT),
            "--output",
            str(target),
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        raw = target.read_bytes()
        self.assertNotIn(b"\r", raw)
        text = raw.decode("utf-8")
        source_lines = (
            with_crlf(RAW_WINDOWS.read_bytes()).decode("utf-8").replace("\r\n", "\n")
        )
        expected_lines = [
            "SF:" + line[3:].replace("\\", "/") if line.startswith("SF:") else line
            for line in source_lines.split("\n")
        ]
        self.assertEqual(text.split("\n"), expected_lines)
        self.assertTrue(text.endswith("\n"))
        self.assertFalse(text.endswith("\n\n"))
        _ = text.encode("utf-8")

    def test_empty_fixture_is_rejected_without_touching_file(self):
        original = self.copy_fixture(EMPTY)
        result = run_normalizer("--report", str(self.report), "--repo-root", str(ROOT))
        self.assertEqual(result.returncode, 1)
        self.assertIn("Отчёт пуст", result.stderr)
        self.assertEqual(self.report.read_bytes(), original)


if __name__ == "__main__":
    _ = unittest.main()
