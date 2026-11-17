"""Регрессии проверки уязвимостей: инвентарь, коды выхода и охват источников.

Настоящий сканер не требуется: подменный исполняемый файл возвращает заданные
коды и отчёты, поэтому проверяются именно решения проверки — пустой охват,
неразобранный формат, отказ инструмента и находки не превращаются в успех.
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import override

from check_osv import (
    CHECK_ERROR_CODE,
    CHECK_FINDINGS_CODE,
    CLEAN_EXIT_CODE,
    CoverageError,
    Source,
    audit,
    check_source,
    inventory,
    parse_lockfile,
    parse_requirements,
    vulnerabilities,
)

FAKE_SCANNER = """import json
import os
import sys

plan_path = os.environ["FAKE_OSV_PLAN"]
with open(plan_path, encoding="utf-8") as handle:
    plan = json.load(handle)
arguments = sys.argv[1:]
lockfile = arguments[arguments.index("--lockfile") + 1]
entry = plan.get(lockfile, {"code": 128, "results": []})
print(json.dumps({"results": entry["results"]}))
sys.exit(entry["code"])
"""

LOCK_TEMPLATE = """{
  "version": 2,
  "dependencies": {
    "net10.0": {
      "Direct.Package": {
        "type": "Direct",
        "requested": "[1.0.0, )",
        "resolved": "1.0.0"
      },
      "Transitive.Package": {
        "type": "Transitive",
        "resolved": "2.0.0"
      },
      "Open.Package": {
        "type": "Transitive",
        "resolved": "3.0.0"
      },
      "Project.Reference": {
        "type": "Project"
      }
    }
  }
}
"""

EMPTY_LOCK = """{
  "version": 2,
  "dependencies": {
    "net10.0": {}
  }
}
"""

ADVISORY = {"id": "GHSA-aaaa-bbbb-cccc", "modified": "2026-01-01T00:00:00Z"}


class RepositoryCase(unittest.TestCase):
    """Временный Git-репозиторий с подменным сканером."""

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()
        self.scanner: Path = Path()
        self.plan: Path = Path()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.scanner = self.root / "fake_osv.py"
        _ = self.scanner.write_text(FAKE_SCANNER, encoding="utf-8")
        self.plan = self.root / "plan.json"
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "f@example.invalid")
        self.git("config", "user.name", "fixture")

    def git(self, *args: str) -> None:
        _ = subprocess.run(  # noqa: S603
            ["git", "-C", str(self.root), *args],  # noqa: S607
            check=True,
            capture_output=True,
        )

    def write(self, name: str, text: str) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")
        return path

    def stage(self) -> None:
        self.git("add", "-A")

    def binary(self) -> list[str]:
        return [sys.executable, str(self.scanner)]

    def run_plan(self, plan: dict[str, object]) -> None:
        _ = self.plan.write_text(json.dumps(plan), encoding="utf-8")
        os.environ["FAKE_OSV_PLAN"] = str(self.plan)
        self.addCleanup(os.environ.pop, "FAKE_OSV_PLAN", "")

    def source(self, name: str = "src/Lib/packages.lock.json") -> Source:
        _ = self.write(name, LOCK_TEMPLATE)
        self.stage()
        return inventory(self.root)[0]


class InventoryTests(RepositoryCase):
    def test_lockfile_inventory_includes_transitive_and_skips_projects(self) -> None:
        path = self.write("src/Lib/packages.lock.json", LOCK_TEMPLATE)
        source = parse_lockfile(path, "src/Lib/packages.lock.json")
        self.assertEqual(
            sorted(source.packages),
            [
                ("Direct.Package", "1.0.0"),
                ("Open.Package", "3.0.0"),
                ("Transitive.Package", "2.0.0"),
            ],
        )
        self.assertEqual(source.ecosystem, "nuget")

    def test_empty_lockfile_is_valid_empty_inventory(self) -> None:
        path = self.write("src/Core/packages.lock.json", EMPTY_LOCK)
        self.assertEqual(
            parse_lockfile(path, "src/Core/packages.lock.json").packages, frozenset()
        )

    def test_invalid_lockfiles_are_coverage_errors(self) -> None:
        broken = self.write("a/packages.lock.json", "{")
        with self.assertRaises(CoverageError):
            _ = parse_lockfile(broken, "a/packages.lock.json")
        wrong = self.write("b/packages.lock.json", json.dumps({"version": 2}))
        with self.assertRaises(CoverageError):
            _ = parse_lockfile(wrong, "b/packages.lock.json")
        no_versions = self.write(
            "c/packages.lock.json",
            json.dumps({"dependencies": {"net10.0": {"P": {"type": "Direct"}}}}),
        )
        with self.assertRaises(CoverageError):
            _ = parse_lockfile(no_versions, "c/packages.lock.json")

    def test_requirements_require_exact_versions(self) -> None:
        pinned = self.write(
            "req/requirements.txt", "# комментарий\nruf==0.17.5\npyyaml==6.0.3\n\n"
        )
        source = parse_requirements(pinned, "req/requirements.txt")
        self.assertEqual(
            source.packages, frozenset({("ruf", "0.17.5"), ("pyyaml", "6.0.3")})
        )
        self.assertEqual(
            source.lockfile_argument(), "requirements.txt:req/requirements.txt"
        )
        loose = self.write("req/other.txt", "ruff>=0.17.5\n")
        with self.assertRaises(CoverageError):
            _ = parse_requirements(loose, "req/other.txt")

    def test_inventory_uses_tracked_files_only(self) -> None:
        _ = self.write("src/Lib/packages.lock.json", LOCK_TEMPLATE)
        _ = self.write("tools/requirements.txt", "ruff==0.17.5\n")
        _ = self.write("ignored/packages.lock.json", LOCK_TEMPLATE)
        self.stage()
        self.git("rm", "-q", "--cached", "ignored/packages.lock.json")
        sources = inventory(self.root)
        self.assertEqual(
            [source.path for source in sources],
            ["src/Lib/packages.lock.json", "tools/requirements.txt"],
        )

    def test_absent_manifests_are_coverage_error(self) -> None:
        with self.assertRaises(CoverageError):
            _ = inventory(self.root)


class ExitCodeTests(RepositoryCase):
    def test_clean_source_passes(self) -> None:
        source = self.source()
        self.run_plan({source.lockfile_argument(): {"code": 0, "results": []}})
        self.assertEqual(check_source(self.binary(), source, self.root), [])

    def test_findings_are_reported_without_absolute_paths(self) -> None:
        source = self.source()
        self.run_plan(
            {
                source.lockfile_argument(): {
                    "code": 1,
                    "results": [
                        {
                            "source": {
                                "path": "/home/runner/work/repo/src/Lib/packages.lock.json"
                            },
                            "packages": [
                                {
                                    "package": {
                                        "name": "Transitive.Package",
                                        "version": "2.0.0",
                                        "ecosystem": "NuGet",
                                    },
                                    "vulnerabilities": [ADVISORY],
                                }
                            ],
                        }
                    ],
                }
            }
        )
        findings = check_source(self.binary(), source, self.root)
        self.assertEqual(
            findings,
            [
                "GHSA-aaaa-bbbb-cccc источник=src/Lib/packages.lock.json пакет=Transitive.Package версия=2.0.0"
            ],
        )
        self.assertNotIn("/home/runner", findings[0])

    def test_scanner_error_and_empty_result_never_pass(self) -> None:
        source = self.source()
        self.run_plan({source.lockfile_argument(): {"code": 127, "results": []}})
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)
        self.run_plan({source.lockfile_argument(): {"code": 128, "results": []}})
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)

    def test_empty_lock_requires_no_packages_code(self) -> None:
        _ = self.write("src/Core/packages.lock.json", EMPTY_LOCK)
        self.stage()
        source = inventory(self.root)[0]
        self.run_plan({source.lockfile_argument(): {"code": 128, "results": []}})
        self.assertEqual(check_source(self.binary(), source, self.root), [])
        self.run_plan({source.lockfile_argument(): {"code": 0, "results": []}})
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)

    def test_inconsistent_reports_are_coverage_errors(self) -> None:
        source = self.source()
        self.run_plan({source.lockfile_argument(): {"code": 0, "results": []}})
        with self.assertRaises(CoverageError):
            _ = vulnerabilities("не json", source)
        with self.assertRaises(CoverageError):
            _ = vulnerabilities("{}", source)
        self.run_plan(
            {
                source.lockfile_argument(): {
                    "code": 0,
                    "results": [
                        {"source": {"path": "other/packages.lock.json"}, "packages": []}
                    ],
                }
            }
        )
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)
        self.run_plan(
            {
                source.lockfile_argument(): {
                    "code": 0,
                    "results": [
                        {
                            "source": {"path": "src/Lib/packages.lock.json"},
                            "packages": [
                                {
                                    "package": {"name": "P", "version": "1"},
                                    "vulnerabilities": [ADVISORY],
                                }
                            ],
                        }
                    ],
                }
            }
        )
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)
        self.run_plan({source.lockfile_argument(): {"code": 1, "results": []}})
        with self.assertRaises(CoverageError):
            _ = check_source(self.binary(), source, self.root)

    def test_missing_scanner_binary_is_coverage_error(self) -> None:
        source = self.source()
        with self.assertRaises(CoverageError):
            _ = check_source([str(self.root / "absent")], source, self.root)

    def test_audit_reports_every_source_and_counts(self) -> None:
        _ = self.write("src/Lib/packages.lock.json", LOCK_TEMPLATE)
        _ = self.write("tools/requirements.txt", "ruff==0.17.5\n")
        _ = self.write("src/Core/packages.lock.json", EMPTY_LOCK)
        self.stage()
        self.run_plan(
            {
                "src/Lib/packages.lock.json": {"code": 0, "results": []},
                "requirements.txt:tools/requirements.txt": {"code": 0, "results": []},
                "src/Core/packages.lock.json": {"code": 128, "results": []},
            }
        )
        sources, findings = audit(self.binary(), self.root)
        self.assertEqual([len(source.packages) for source in sources], [0, 3, 1])
        self.assertEqual(findings, [])

    def test_audit_without_any_package_is_coverage_error(self) -> None:
        _ = self.write("src/Core/packages.lock.json", EMPTY_LOCK)
        self.stage()
        self.run_plan({"src/Core/packages.lock.json": {"code": 128, "results": []}})
        with self.assertRaises(CoverageError):
            _ = audit(self.binary(), self.root)

    def test_report_codes_are_distinct(self) -> None:
        self.assertNotEqual(CHECK_FINDINGS_CODE, CLEAN_EXIT_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, CHECK_FINDINGS_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, CLEAN_EXIT_CODE)
