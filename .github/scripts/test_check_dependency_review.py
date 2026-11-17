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
        self.assertIn(
            ("NuGet", "Npgsql", "10.0.3", "src/Lib/packages.lock.json"), found
        )
        self.assertIn(("NuGet", "Serilog", "4.0.0", "Directory.Packages.props"), found)
        self.assertIn(
            ("pip", "ruff", "0.17.5", ".github/scripts/requirements.txt"), found
        )
        self.assertIn(
            ("actions", "actions/checkout", "a" * 40, ".github/workflows/ci.yml"), found
        )

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
    def test_version_swap_between_manifests_preserves_both_removals_and_additions(
        self,
    ) -> None:
        for name, version in (("first", "1.0"), ("second", "2.0")):
            self.write(f"{name}/requirements.txt", f"package=={version}\n")
        base = self.commit("исходные версии")
        for name, version in (("first", "2.0"), ("second", "1.0")):
            self.write(f"{name}/requirements.txt", f"package=={version}\n")
        head = self.commit("обмен версиями")
        found = changes(dependencies(self.root, base), dependencies(self.root, head))
        self.assertEqual(
            set(found),
            {
                Change("removed", "pip", "package", "1.0", "first/requirements.txt"),
                Change("added", "pip", "package", "2.0", "first/requirements.txt"),
                Change("removed", "pip", "package", "2.0", "second/requirements.txt"),
                Change("added", "pip", "package", "1.0", "second/requirements.txt"),
            },
        )
        with self.assertRaises(CoverageError):
            _ = verify(found, found[:2])
        self.assertIn("4 изменений", verify(found, found)[0])

    def test_identical_sets_have_no_changes(self) -> None:
        base = {("NuGet", "Npgsql", "10.0.3", "packages.lock.json")}
        self.assertEqual(changes(base, set(base)), [])

    def test_added_and_removed_manifests_preserve_identity(self) -> None:
        entry = ("pip", "package", "1.0", "tools/requirements.txt")
        self.assertEqual(changes(set(), {entry}), [Change("added", *entry)])
        self.assertEqual(changes({entry}, set()), [Change("removed", *entry)])


class ComparisonTests(unittest.TestCase):
    def test_real_api_update_requires_removed_and_added_with_exact_versions(
        self,
    ) -> None:
        expected = changes(
            {("NuGet", "Npgsql", "10.0.3", "src/packages.lock.json")},
            {("NuGet", "Npgsql", "10.0.4", "src/packages.lock.json")},
        )
        # Форма из ChangeSchema закреплённого действия; updated API не возвращает.
        payload = [
            {
                "change_type": "removed",
                "ecosystem": "nuget",
                "name": "Npgsql",
                "version": "10.0.3",
                "manifest": "src/packages.lock.json",
            },
            {
                "change_type": "added",
                "ecosystem": "nuget",
                "name": "Npgsql",
                "version": "10.0.4",
                "manifest": "src/packages.lock.json",
            },
        ]
        self.assertEqual(
            verify(expected, reported(json.dumps(payload))),
            ["покрытие подтверждено для 2 изменений"],
        )
        for index in range(len(payload)):
            with self.subTest(missing=index), self.assertRaises(CoverageError):
                _ = verify(
                    expected,
                    reported(json.dumps(payload[:index] + payload[index + 1 :])),
                )
            for field, wrong in (
                ("version", "9.9"),
                ("manifest", "other/packages.lock.json"),
                ("change_type", "added" if index == 0 else "removed"),
            ):
                broken = [dict(item) for item in payload]
                broken[index][field] = wrong
                with (
                    self.subTest(index=index, field=field),
                    self.assertRaises(CoverageError),
                ):
                    _ = verify(expected, reported(json.dumps(broken)))

    def test_zero_changes_passes_without_claiming_audit(self) -> None:
        self.assertEqual(
            verify([], []), ["изменений зависимостей в PR нет; проверять нечего"]
        )
        self.assertEqual(reported(""), [])

    def test_actions_ecosystem_is_never_claimed_as_covered(self) -> None:
        expected = [
            Change(
                "added",
                "actions",
                "actions/checkout",
                "a" * 40,
                ".github/workflows/new.yml",
            )
        ]
        for received in ([], expected):
            notes = verify(expected, received)
            self.assertIn("0 изменений", notes[0])
            self.assertIn("не подтверждается", notes[1])
            self.assertIn("new.yml", notes[1])

    def test_broken_output_is_coverage_error(self) -> None:
        for text in (
            "{",
            '{"changes": []}',
            "[1]",
            "[{}]",
            '[{"change_type":"updated","ecosystem":"pip","name":"p","version":"1","manifest":"requirements.txt"}]',
        ):
            with self.subTest(text=text), self.assertRaises(CoverageError):
                _ = reported(text)

    def test_imprecise_versions_are_reported(self) -> None:
        found = imprecise(
            [
                Change("added", "NuGet", "Npgsql", ">= 0", "packages.lock.json"),
                Change("added", "pip", "httpx", "0.28.0", "requirements.txt"),
            ]
        )
        self.assertEqual(found, ["Npgsql >= 0"])

    def test_report_codes_are_distinct(self) -> None:
        self.assertNotEqual(CHECK_FINDINGS_CODE, CHECK_ERROR_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, 0)
