"""Регрессии сверки охвата Dependency Review.

Действие GitHub здесь не запускается: изменения зависимостей считаются из
синтетического Git-репозитория, а вывод действий формируется как фикстура.
Проверяется, что пропущенное изменение, неточная версия и повреждённый вывод
не дают ложного успеха, а согласованное нулевое изменение проходит.
"""

import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from typing import override

from check_dependency_review import (
    CHECK_ERROR_CODE,
    CHECK_FINDINGS_CODE,
    Change,
    CoverageError,
    changes,
    dependencies,
    imprecise,
    reported,
    verify,
)

LOCK = """{
  "version": 2,
  "dependencies": {
    "net10.0": {
      "%s": {
        "type": "Direct",
        "resolved": "%s"
      }
    }
  }
}
"""
CENTRAL = '<Project><ItemGroup>\n  <PackageVersion Include="%s" Version="%s" />\n</ItemGroup></Project>\n'
WORKFLOW = "steps:\n  - uses: actions/checkout@%s\n"


class RepositoryCase(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        _ = self.git("init", "-q", "-b", "main")
        _ = self.git("config", "user.email", "f@example.invalid")
        _ = self.git("config", "user.name", "fixture")

    def git(self, *args: str) -> str:
        result = subprocess.run(  # noqa: S603
            ["git", "-C", str(self.root), *args],  # noqa: S607
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        return result.stdout.strip()

    def write(self, name: str, text: str) -> None:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")

    def commit(self, message: str) -> str:
        _ = self.git("add", "-A")
        _ = self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD")


class DependencySetTests(RepositoryCase):
    def test_versions_are_read_from_all_supported_manifests(self) -> None:
        self.write("src/Lib/packages.lock.json", LOCK % ("Npgsql", "10.0.3"))
        self.write("Directory.Packages.props", CENTRAL % ("Serilog", "4.0.0"))
        self.write(".github/scripts/requirements.txt", "ruff==0.17.5\n")
        self.write(".github/workflows/ci.yml", WORKFLOW % ("a" * 40))
        revision = self.commit("зависимости")
        found = dependencies(self.root, revision)
        self.assertIn(("NuGet", "Npgsql", "10.0.3"), found)
        self.assertIn(("NuGet", "Serilog", "4.0.0"), found)
        self.assertIn(("pip", "ruff", "0.17.5"), found)
        self.assertIn(("actions", "actions/checkout", "a" * 40), found)

    def test_unknown_revision_is_coverage_error(self) -> None:
        _ = self.write("Directory.Packages.props", CENTRAL % ("Serilog", "4.0.0"))
        revision = self.commit("зависимости")
        with self.assertRaises(CoverageError):
            _ = dependencies(self.root, "9" * 40)
        self.assertTrue(dependencies(self.root, revision))

    def test_broken_lockfile_in_revision_is_coverage_error(self) -> None:
        _ = self.write("src/Lib/packages.lock.json", "{")
        revision = self.commit("сломанный lock")
        with self.assertRaises(CoverageError):
            _ = dependencies(self.root, revision)


class ChangeTests(RepositoryCase):
    def test_changes_cover_added_removed_and_updated(self) -> None:
        base = {("NuGet", "Npgsql", "10.0.3"), ("NuGet", "Serilog", "4.0.0")}
        head = {("NuGet", "Npgsql", "10.0.4"), ("pip", "ruff", "0.17.5")}
        found = changes(base, head)
        self.assertEqual(
            [change.describe() for change in found],
            [
                "updated NuGet Npgsql 10.0.4",
                "removed NuGet Serilog 4.0.0",
                "added pip ruff 0.17.5",
            ],
        )

    def test_identical_sets_have_no_changes(self) -> None:
        base = {("NuGet", "Npgsql", "10.0.3")}
        self.assertEqual(changes(base, set(base)), [])


class ComparisonTests(unittest.TestCase):
    def test_verified_coverage_passes(self) -> None:
        expected = [Change("updated", "NuGet", "Npgsql", "10.0.4")]
        reported_changes = [Change("updated", "nuget", "Npgsql", "10.0.4")]
        self.assertEqual(
            verify(expected, reported_changes),
            ["покрытие подтверждено для 1 изменений"],
        )
        self.assertEqual(
            verify([], []), ["изменений зависимостей в PR нет; проверять нечего"]
        )

    def test_missing_change_is_coverage_error(self) -> None:
        expected = [
            Change("updated", "NuGet", "Npgsql", "10.0.4"),
            Change("added", "pip", "httpx", "0.28.0"),
        ]
        with self.assertRaises(CoverageError) as context:
            _ = verify(expected, [Change("updated", "nuget", "Npgsql", "10.0.4")])
        self.assertIn("httpx", str(context.exception))

    def test_actions_ecosystem_is_reported_but_not_claimed_as_covered(self) -> None:
        expected = [
            Change("added", "actions", "actions/dependency-review-action", "a1d282b"),
            Change("added", "pip", "httpx", "0.28.0"),
        ]
        notes = verify(expected, [Change("added", "pip", "httpx", "0.28.0")])
        self.assertEqual(notes[0], "покрытие подтверждено для 1 изменений")
        self.assertIn("githubactions не подтверждается графом PR", notes[1])
        self.assertIn("actions/dependency-review-action", notes[1])

    def test_unconfirmed_actions_change_alone_does_not_fail(self) -> None:
        notes = verify([Change("added", "actions", "actions/checkout", "3d3c42e")], [])
        self.assertEqual(notes[0], "покрытие подтверждено для 0 изменений")
        self.assertIn("githubactions", notes[1])

    def test_empty_report_with_expected_changes_is_error(self) -> None:
        with self.assertRaises(CoverageError):
            _ = verify([Change("added", "pip", "httpx", "0.28.0")], [])

    def test_output_parsing_handles_action_and_api_shapes(self) -> None:
        action_shape = json.dumps(
            [
                {
                    "change-type": "added",
                    "ecosystem": "pip",
                    "name": "httpx",
                    "version": "0.28.0",
                }
            ]
        )
        api_shape = json.dumps(
            [
                {
                    "change_type": "added",
                    "ecosystem": "pip",
                    "name": "httpx",
                    "version": "0.28.0",
                }
            ]
        )
        expected = [Change("added", "pip", "httpx", "0.28.0")]
        self.assertEqual(len(reported(action_shape)), 1)
        self.assertEqual(len(reported(api_shape)), 1)
        _ = verify(expected, reported(action_shape))
        _ = verify(expected, reported(api_shape))
        self.assertEqual(reported(""), [])
        self.assertEqual(reported("   "), [])

    def test_broken_output_is_coverage_error(self) -> None:
        with self.assertRaises(CoverageError):
            _ = reported("{")
        with self.assertRaises(CoverageError):
            _ = reported(json.dumps({"changes": []}))
        with self.assertRaises(CoverageError):
            _ = reported(json.dumps([1]))

    def test_imprecise_versions_are_reported(self) -> None:
        found = imprecise(
            [
                Change("added", "NuGet", "Npgsql", ">= 0"),
                Change("added", "pip", "httpx", "0.28.0"),
            ]
        )
        self.assertEqual(found, ["Npgsql >= 0"])

    def test_report_codes_are_distinct(self) -> None:
        self.assertNotEqual(CHECK_FINDINGS_CODE, CHECK_ERROR_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, 0)
