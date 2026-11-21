"""Поведенческие проверки нормализатора путей Cobertura .NET.

Скрипт запускается по отслеживаемому пути, поэтому его точка входа покрывается
тестами, а не рабочим прогоном CI.
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
from xml.etree import ElementTree
from xml.sax.saxutils import escape, quoteattr

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / ".github/scripts"
FIXTURES = SCRIPTS / "fixtures/coverage_workflow"
NORMALIZER = SCRIPTS / "normalize_cobertura_paths.py"
CORRUPT = FIXTURES / "cobertura-corrupt.xml"
SOURCE = "src/AnketaParser.Core/ExitCodes.cs"


def build_report(*filenames: str) -> str:
    """Собрать минимальный Cobertura с указанными путями классов."""
    classes = "".join(
        f'<class name="Type{index}" filename={quoteattr(name)} line-rate="1" branch-rate="0"><methods/><lines><line number="1" hits="1"/></lines></class>'
        for index, name in enumerate(filenames)
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<coverage line-rate="1" branch-rate="0" lines-valid="1" lines-covered="1"'
        ' branches-valid="0" branches-covered="0" version="1.9" timestamp="0">'
        f'<packages><package name="AnketaParser.Core" line-rate="1"'
        f' branch-rate="0"><classes>{classes}</classes></package></packages>'
        "</coverage>\n"
    )


class CoberturaNormalizerTests(unittest.TestCase):
    directory: Path
    report: Path

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.directory = Path()
        self.report = Path()

    @override
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.report = self.directory / "dotnet.cobertura.xml"
        self.addCleanup(shutil.rmtree, self.directory, True)

    def run_normalizer(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(  # noqa: S603 - фиксированная команда без shell
            [
                sys.executable,
                str(NORMALIZER),
                "--report",
                str(self.report),
                "--repo-root",
                str(ROOT),
                *arguments,
            ],
            cwd=ROOT,
            env={**os.environ, "PYTHONIOENCODING": "utf-8"},
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

    def filenames(self) -> list[str]:
        document = ElementTree.parse(self.report)  # noqa: S314
        return [element.get("filename") or "" for element in document.iter("class")]

    def test_absolute_runner_paths_become_repository_relative(self):
        absolute = f"{ROOT.as_posix()}/{SOURCE}"
        _ = self.report.write_text(build_report(absolute), encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.filenames(), [SOURCE])
        self.assertIn("переписано путей 1", result.stdout)
        self.assertIn("отвергнуто 0", result.stdout)

    def test_windows_absolute_path_is_rewritten(self):
        absolute = "C:\\build\\anketa-parser\\src\\AnketaParser.Core\\ExitCodes.cs"
        _ = self.report.write_text(build_report(absolute), encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.filenames(), [SOURCE])

    def test_src_ancestors_do_not_hide_the_repository_source_path(self):
        nested_root = self.directory / "src" / "repo"
        source = nested_root / SOURCE
        source.parent.mkdir(parents=True)
        _ = source.write_text("class Source {}\n", encoding="utf-8")
        foreign = f"/foreign/src/repo/{SOURCE}"
        _ = self.report.write_text(
            build_report(source.as_posix(), foreign), encoding="utf-8"
        )
        result = self.run_normalizer("--repo-root", str(nested_root))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.filenames(), [SOURCE, SOURCE])

        ambiguous = nested_root / "src/repo" / SOURCE
        ambiguous.parent.mkdir(parents=True)
        ambiguous.touch()
        _ = self.report.write_text(build_report(foreign), encoding="utf-8")
        rejected = self.run_normalizer("--repo-root", str(nested_root))
        self.assertEqual(rejected.returncode, 1, rejected.stdout)
        self.assertIn("неоднозначный путь", rejected.stderr)

    def test_second_run_is_idempotent(self):
        absolute = f"{ROOT.as_posix()}/{SOURCE}"
        _ = self.report.write_text(build_report(absolute), encoding="utf-8")
        first = self.run_normalizer()
        self.assertEqual(first.returncode, 0, first.stderr)
        body = self.report.read_bytes()
        second = self.run_normalizer()
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(self.report.read_bytes(), body)
        self.assertIn("переписано путей 0", second.stdout)
        self.assertIn("уже относительных 1", second.stdout)

    def test_paths_outside_src_and_traversal_are_rejected(self):
        cases = (
            ("C:\\build\\anketa-parser\\tests\\Fake.cs", "вне src"),
            ("/build/../etc/passwd", "выход за пределы репозитория"),
            ("src/AnketaParser.Core/Absent.cs", "исходник не найден"),
        )
        for name, message in cases:
            with self.subTest(name=name):
                original = build_report(name)
                _ = self.report.write_text(original, encoding="utf-8")
                result = self.run_normalizer()
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn(message, result.stderr)
                self.assertEqual(self.report.read_text(encoding="utf-8"), original)

    def test_document_with_sources_is_rejected(self):
        body = build_report(SOURCE).replace(
            "</coverage>",
            "<sources><source>"
            + escape(ROOT.as_posix() + "/src/")
            + "</source></sources></coverage>",
        )
        _ = self.report.write_text(body, encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 1)
        self.assertIn("<sources>", result.stderr)

    def test_corrupt_empty_and_doctype_documents_are_rejected(self):
        for body, message in (
            (CORRUPT.read_text(encoding="utf-8"), "Повреждённый отчёт"),
            ("", "Отчёт пуст"),
            ("<!DOCTYPE x><coverage><packages/></coverage>", "DOCTYPE"),
            ('<?xml version="1.0"?><other/>', "вместо coverage"),
        ):
            with self.subTest(message=message):
                _ = self.report.write_text(body, encoding="utf-8")
                result = self.run_normalizer()
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertIn(message, result.stderr)

    def test_relative_path_outside_src_is_not_remapped(self):
        body = build_report("tests/fixtures/src/AnketaParser.Core/ExitCodes.cs")
        _ = self.report.write_text(body, encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("путь вне src", result.stderr)
        self.assertEqual(self.report.read_text(encoding="utf-8"), body)

    def test_non_positive_size_limit_is_rejected_before_reading(self):
        _ = self.report.write_text(build_report(SOURCE), encoding="utf-8")
        result = self.run_normalizer("--max-bytes", "-2")
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn("предел размера должен быть больше нуля", result.stderr)

    def test_success_leaves_no_temporary_files(self):
        absolute = f"{ROOT.as_posix()}/{SOURCE}"
        _ = self.report.write_text(build_report(absolute), encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            [path.name for path in self.directory.iterdir()], [self.report.name]
        )

    def test_class_without_filename_attribute_is_rejected(self):
        body = build_report(SOURCE).replace('filename="' + SOURCE + '"', "", 1)
        _ = self.report.write_text(body, encoding="utf-8")
        result = self.run_normalizer()
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("класс без атрибута filename", result.stderr)
        self.assertEqual(self.report.read_text(encoding="utf-8"), body)

    def test_report_over_size_limit_is_rejected_before_parsing(self):
        _ = self.report.write_text(build_report(SOURCE), encoding="utf-8")
        result = self.run_normalizer("--max-bytes", "64")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("превышает допустимый размер", result.stderr)

    def test_output_keeps_structure_and_writes_requested_file(self):
        absolute = f"{ROOT.as_posix()}/{SOURCE}"
        _ = self.report.write_text(
            build_report(absolute, f"{ROOT.as_posix()}/{SOURCE}"), encoding="utf-8"
        )
        target = self.directory / "normalized.xml"
        result = self.run_normalizer("--output", str(target))
        self.assertEqual(result.returncode, 0, result.stderr)
        document = ElementTree.parse(target)  # noqa: S314
        classes = list(document.iter("class"))
        self.assertEqual(len(classes), 2)
        self.assertEqual(
            [element.get("filename") for element in classes], [SOURCE, SOURCE]
        )
        self.assertEqual(document.getroot().get("lines-valid"), "1")
        self.assertEqual(len(list(document.iter("line"))), 2)


if __name__ == "__main__":
    _ = unittest.main()
