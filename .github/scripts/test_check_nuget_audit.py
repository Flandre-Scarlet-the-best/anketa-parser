"""Регрессии проверки NuGet Audit: охват проектов, транзитивный режим и advisories.

Отчёт формируется как фикстура, поэтому проверяются решения проверки: пропуск
проекта, отсутствие транзитивного режима, повреждённый отчёт и найденный
advisory не могут дать ложный успех.
"""

import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from typing import override

from check_nuget_audit import (
    CHECK_ERROR_CODE,
    CHECK_FINDINGS_CODE,
    CLEAN_EXIT_CODE,
    CoverageError,
    check,
    compare_projects,
    parse_report,
)

PARAMETERS = "--vulnerable --include-transitive"
SOURCES = ["https://api.nuget.org/v3/index.json"]


class ReportCase(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.git("init", "-q", "-b", "main")

    def git(self, *args: str) -> None:
        _ = subprocess.run(  # noqa: S603
            ["git", "-C", str(self.root), *args],  # noqa: S607
            check=True,
            capture_output=True,
        )

    def project(self, name: str) -> None:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text("<Project />", encoding="utf-8")

    def report(self, projects: list[dict[str, object]]) -> str:
        return json.dumps(
            {
                "version": 1,
                "parameters": PARAMETERS,
                "sources": SOURCES,
                "projects": projects,
            }
        )

    def written(self, text: str) -> Path:
        path = self.root / "report.json"
        _ = path.write_text(text, encoding="utf-8")
        return path

    def absolute(self, name: str) -> str:
        return (self.root / name).as_posix()


class ParseTests(ReportCase):
    def test_clean_report_lists_every_project(self) -> None:
        self.project("src/Lib/Lib.csproj")
        self.git("add", "-A")
        text = self.report([{"path": self.absolute("src/Lib/Lib.csproj")}])
        projects, advisories = parse_report(text, self.root)
        self.assertEqual(projects, ["src/Lib/Lib.csproj"])
        self.assertEqual(advisories, [])

    def test_advisories_include_kind_and_severity(self) -> None:
        self.project("src/Lib/Lib.csproj")
        self.git("add", "-A")
        text = self.report(
            [
                {
                    "path": self.absolute("src/Lib/Lib.csproj"),
                    "frameworks": [
                        {
                            "framework": "net10.0",
                            "transitivePackages": [
                                {
                                    "id": "Newtonsoft.Json",
                                    "resolvedVersion": "12.0.2",
                                    "vulnerabilities": [
                                        {
                                            "severity": "High",
                                            "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr",
                                        }
                                    ],
                                }
                            ],
                        }
                    ],
                }
            ]
        )
        projects, advisories = parse_report(text, self.root)
        self.assertEqual(projects, ["src/Lib/Lib.csproj"])
        self.assertEqual(
            advisories,
            [
                "https://github.com/advisories/GHSA-5crp-9r3c-p9vr проект=src/Lib/Lib.csproj пакет=Newtonsoft.Json версия=12.0.2 severity=High тип=transitive"
            ],
        )

    def test_vulnerabilities_must_be_a_list_when_present(self) -> None:
        self.project("a.csproj")
        self.git("add", "-A")
        package: dict[str, object] = {"id": "Package", "resolvedVersion": "1"}
        projects: list[dict[str, object]] = [
            {
                "path": self.absolute("a.csproj"),
                "frameworks": [{"transitivePackages": [package]}],
            }
        ]
        path = self.written(self.report(projects))
        self.assertEqual(check(path, self.root)[1], [])
        invalid_and_empty: list[object] = [{}, None, "invalid", []]
        for value in invalid_and_empty:
            package["vulnerabilities"] = value
            path = self.written(self.report(projects))
            if isinstance(value, list):
                self.assertEqual(check(path, self.root)[1], [])
            else:
                with self.subTest(value=value), self.assertRaises(CoverageError):
                    _ = check(path, self.root)

    def test_missing_transitive_mode_or_projects_is_coverage_error(self) -> None:
        without_transitive = json.dumps(
            {
                "version": 1,
                "parameters": "--vulnerable",
                "sources": SOURCES,
                "projects": [{"path": self.absolute("a.csproj")}],
            }
        )
        with self.assertRaises(CoverageError):
            _ = parse_report(without_transitive, self.root)
        # Отчёт без `--vulnerable` не содержит ключа vulnerabilities, поэтому его
        # пустой список advisories не доказывает отсутствие уязвимостей.
        without_vulnerable = json.dumps(
            {
                "version": 1,
                "parameters": "--include-transitive",
                "sources": SOURCES,
                "projects": [{"path": self.absolute("a.csproj")}],
            }
        )
        with self.assertRaises(CoverageError):
            _ = parse_report(without_vulnerable, self.root)
        empty = self.report([])
        with self.assertRaises(CoverageError):
            _ = parse_report(empty, self.root)
        without_sources = json.dumps(
            {
                "version": 1,
                "parameters": PARAMETERS,
                "sources": [],
                "projects": [{"path": "x"}],
            }
        )
        with self.assertRaises(CoverageError):
            _ = parse_report(without_sources, self.root)

    def test_malformed_reports_are_coverage_errors(self) -> None:
        with self.assertRaises(CoverageError):
            _ = parse_report("{", self.root)
        with self.assertRaises(CoverageError):
            _ = parse_report("[]", self.root)
        with self.assertRaises(CoverageError):
            _ = parse_report(
                json.dumps(
                    {
                        "version": 2,
                        "parameters": PARAMETERS,
                        "sources": SOURCES,
                        "projects": [{"path": "x"}],
                    }
                ),
                self.root,
            )
        with self.assertRaises(CoverageError):
            _ = parse_report(
                json.dumps(
                    {
                        "version": 1,
                        "parameters": PARAMETERS,
                        "sources": SOURCES,
                        "projects": [
                            {"path": self.absolute("a.csproj")},
                            {"path": self.absolute("a.csproj")},
                        ],
                    }
                ),
                self.root,
            )
        with self.assertRaises(CoverageError):
            _ = parse_report(
                json.dumps(
                    {
                        "version": 1,
                        "parameters": PARAMETERS,
                        "sources": SOURCES,
                        "projects": ["строка"],
                    }
                ),
                self.root,
            )

    def test_project_outside_repository_is_rejected(self) -> None:
        self.project("a.csproj")
        self.git("add", "-A")
        with self.assertRaises(CoverageError):
            _ = parse_report(
                self.report([{"path": "C:/elsewhere/a.csproj"}]), self.root
            )

    def test_project_coverage_comparison(self) -> None:
        compare_projects({"a.csproj", "b.csproj"}, ["a.csproj", "b.csproj"])
        with self.assertRaises(CoverageError):
            compare_projects({"a.csproj", "b.csproj"}, ["a.csproj"])
        with self.assertRaises(CoverageError):
            compare_projects({"a.csproj"}, ["a.csproj", "c.csproj"])
        with self.assertRaises(CoverageError):
            compare_projects(set(), [])


class FileTests(ReportCase):
    def test_clean_file_passes_and_absent_file_fails(self) -> None:
        self.project("src/Lib/Lib.csproj")
        self.git("add", "-A")
        path = self.written(
            self.report([{"path": self.absolute("src/Lib/Lib.csproj")}])
        )
        projects, advisories = check(path, self.root)
        self.assertEqual(projects, ["src/Lib/Lib.csproj"])
        self.assertEqual(advisories, [])
        with self.assertRaises(CoverageError):
            _ = check(self.root / "absent.json", self.root)

    def test_incomplete_coverage_fails_even_without_advisories(self) -> None:
        self.project("src/Lib/Lib.csproj")
        self.project("tests/Tests/Tests.csproj")
        self.git("add", "-A")
        path = self.written(
            self.report([{"path": self.absolute("src/Lib/Lib.csproj")}])
        )
        with self.assertRaises(CoverageError):
            _ = check(path, self.root)

    def test_report_codes_are_distinct(self) -> None:
        self.assertNotEqual(CHECK_FINDINGS_CODE, CLEAN_EXIT_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, CHECK_FINDINGS_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, CLEAN_EXIT_CODE)
