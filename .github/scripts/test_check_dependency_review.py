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
    NUGET_AUDIT_WORKFLOW,
    Change,
    CoverageError,
    changes,
    dependencies,
    exact_version,
    expected_changes,
    imprecise,
    nuget_audit_proof,
    project_locks,
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


def chosen_locks(project: str, lock: str | None) -> dict[str, str | None]:
    """Выбранный NuGet lock-файл проекта для проверки сверки."""
    return {project: lock}


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
        # Наследуемая подпись коммитов сделала бы фикстуру зависимой от ключей.
        _ = self.git("config", "commit.gpgsign", "false")

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

    def test_quoted_action_reference_keeps_identity(self) -> None:
        name = ".github/workflows/quoted.yml"
        sha = "a" * 40
        self.write(name, f'steps:\n  - uses: "actions/checkout@{sha}"\n')
        revision = self.commit("кавычки в объявлении действия")
        self.assertEqual(
            dependencies(self.root, revision),
            {("actions", "actions/checkout", sha, name)},
        )

    def test_manifest_in_non_ascii_directory_is_not_skipped(self) -> None:
        name = "src/Пакеты/packages.lock.json"
        self.write(name, LOCK % ("Npgsql", "10.0.3"))
        revision = self.commit("манифест в каталоге с не-ASCII именем")
        self.assertEqual(
            dependencies(self.root, revision),
            {("NuGet", "Npgsql", "10.0.3", name)},
        )

    def test_requirements_ignore_comments_and_reject_unproved_lines(self) -> None:
        name = "requirements.txt"
        self.write(
            name, "# Прежняя версия: ruff==0.17.4\nruff==0.17.5  # текущая версия\n"
        )
        first = self.commit("комментарии")
        self.assertEqual(
            dependencies(self.root, first), {("pip", "ruff", "0.17.5", name)}
        )
        self.write(name, "ruff>=0.17.5\n")
        invalid = self.commit("неподдерживаемое требование")
        with self.assertRaises(CoverageError):
            _ = dependencies(self.root, invalid)

    def test_package_version_attribute_order_and_version_change(self) -> None:
        path = "Directory.Packages.props"
        self.write(path, CENTRAL % ("Npgsql", "10.0.3"))
        first = self.commit("исходная версия")
        self.write(
            path,
            '<Project xmlns="urn:msbuild"><ItemGroup><PackageVersion Version="10.0.3" Include="Npgsql" /></ItemGroup></Project>',
        )
        reordered = self.commit("порядок атрибутов")
        self.assertEqual(
            dependencies(self.root, first), dependencies(self.root, reordered)
        )
        self.write(
            path,
            '<Project><PackageVersion Version="10.0.4" Include="Npgsql" /></Project>',
        )
        updated = self.commit("новая версия")
        self.assertEqual(
            set(expected_changes(self.root, reordered, updated)),
            {
                Change("removed", "NuGet", "Npgsql", "10.0.3", path),
                Change("added", "NuGet", "Npgsql", "10.0.4", path),
            },
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

    def test_lock_file_is_chosen_by_nuget_rules(self) -> None:
        self.write("src/Lib/Lib.csproj", "<Project />")
        self.write("src/Lib/packages.lock.json", LOCK % ("Npgsql", "10.0.3"))
        base = self.commit("общий lock каталога")
        self.assertEqual(
            project_locks(self.root, base),
            {"src/Lib/Lib.csproj": "src/Lib/packages.lock.json"},
        )
        self.write("src/Lib/packages.Lib.lock.json", LOCK % ("Npgsql", "10.0.4"))
        named = self.commit("именованный lock проекта")
        self.assertEqual(
            project_locks(self.root, named),
            {"src/Lib/Lib.csproj": "src/Lib/packages.Lib.lock.json"},
        )

    def test_shared_lock_of_two_projects_has_no_proven_owner(self) -> None:
        self.write("src/Lib/First.csproj", "<Project />")
        self.write("src/Lib/Second.csproj", "<Project />")
        self.write("src/Lib/packages.lock.json", LOCK % ("Npgsql", "10.0.3"))
        revision = self.commit("общий lock двух проектов")
        self.assertEqual(
            project_locks(self.root, revision),
            {"src/Lib/First.csproj": None, "src/Lib/Second.csproj": None},
        )

    def test_named_lock_owner_is_not_counted_for_the_shared_file(self) -> None:
        self.write("src/Lib/First.csproj", "<Project />")
        self.write("src/Lib/Second.csproj", "<Project />")
        self.write("src/Lib/packages.First.lock.json", LOCK % ("Npgsql", "10.0.3"))
        self.write("src/Lib/packages.lock.json", LOCK % ("Npgsql", "10.0.4"))
        revision = self.commit("именованный lock одного из проектов")
        self.assertEqual(
            project_locks(self.root, revision),
            {
                "src/Lib/First.csproj": "src/Lib/packages.First.lock.json",
                "src/Lib/Second.csproj": "src/Lib/packages.lock.json",
            },
        )


class ChangeTests(RepositoryCase):
    def test_diverged_base_uses_merge_base(self) -> None:
        self.write("requirements.txt", "ruff==0.17.4\n")
        ancestor = self.commit("общая точка")
        _ = self.git("checkout", "-q", "-b", "task")
        self.write("code.txt", "изменение\n")
        head = self.commit("код")
        _ = self.git("checkout", "-q", "main")
        self.write("requirements.txt", "ruff==0.17.5\n")
        base = self.commit("обновление в основной ветке")
        self.assertNotEqual(
            changes(dependencies(self.root, base), dependencies(self.root, head)), []
        )
        self.assertEqual(expected_changes(self.root, base, head), [])
        self.assertEqual(expected_changes(self.root, ancestor, head), [])
        self.assertEqual(len(expected_changes(self.root, ancestor, base)), 2)

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
            {("pip", "ruff", "0.17.5", "requirements.txt")},
            {("pip", "ruff", "0.17.6", "requirements.txt")},
        )
        # Форма из ChangeSchema закреплённого действия; updated API не возвращает.
        payload = [
            {
                "change_type": "removed",
                "ecosystem": "pip",
                "name": "ruff",
                "version": "0.17.5",
                "manifest": "requirements.txt",
            },
            {
                "change_type": "added",
                "ecosystem": "pip",
                "name": "ruff",
                "version": "0.17.6",
                "manifest": "requirements.txt",
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
                ("manifest", "other/requirements.txt"),
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

    def test_zero_change_contract_requires_empty_report(self) -> None:
        reported_change = Change("added", "pip", "ruff", "0.17.5", "requirements.txt")
        with self.assertRaisesRegex(CoverageError, "которых нет в манифестах"):
            _ = verify([], [reported_change])

    def test_reported_change_outside_manifests_is_a_gap(self) -> None:
        expected = [Change("added", "pip", "ruff", "0.17.5", "requirements.txt")]
        extra = Change("added", "pip", "httpx", "0.28.0", "tools/setup.py")
        with self.assertRaisesRegex(CoverageError, "вне разобранных манифестов"):
            _ = verify(expected, [*expected, extra])
        self.assertEqual(
            verify(expected, expected), ["покрытие подтверждено для 1 изменений"]
        )
        # Неподтверждённая экосистема Actions не превращается в пробел разбора.
        actions = Change("added", "actions", "actions/checkout", "a" * 40, "w.yml")
        self.assertIn("1 изменений", verify(expected, [*expected, actions])[0])

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

    def test_nuget_versions_are_not_claimed_as_confirmed_by_graph(self) -> None:
        # Граф PR этого репозитория не разрешает версии NuGet: их отсутствие в
        # отчёте не является пробелом разбора манифестов и не блокирует проверку.
        expected = [
            Change("added", "NuGet", "Npgsql", "10.0.4", "packages.lock.json"),
            Change("removed", "NuGet", "Npgsql", "10.0.3", "packages.lock.json"),
        ]
        notes = verify(expected, [])
        self.assertEqual(notes[0], "покрытие подтверждено для 0 изменений")
        self.assertIn(
            "экосистема NuGet не подтверждается графом PR: граф не публикует "
            "разрешённые версии из packages.lock.json и Directory.Packages.props (2)",
            notes[1],
        )
        # Совместно с подтверждённой экосистемой неподтверждённые не скрывают её.
        pip = Change("added", "pip", "ruff", "0.17.5", "requirements.txt")
        mixed = verify([*expected, pip], [pip])
        self.assertEqual(mixed[0], "покрытие подтверждено для 1 изменений")
        self.assertIn("экосистема NuGet не подтверждается графом PR", mixed[1])
        self.assertIn("(2)", mixed[1])

    def test_unconfirmed_report_entry_is_not_a_gap(self) -> None:
        expected = [Change("added", "NuGet", "Npgsql", "10.0.4", "packages.lock.json")]
        imprecise_entry = Change(
            "added",
            "NuGet",
            "Npgsql",
            ">= 0",
            "src/AnketaParser.Cli/AnketaParser.Cli.csproj",
        )
        notes = verify(expected, [imprecise_entry])
        self.assertIn("0 изменений", notes[0])

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
            ],
            [],
            {},
            {},
        )
        self.assertEqual(found, ["Npgsql >= 0"])

    def test_nuget_project_placeholder_requires_matching_lock_change(self) -> None:
        graph = Change("added", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        lock = Change(
            "added", "NuGet", "Npgsql", "10.0.4", "src/Lib/packages.lock.json"
        )
        chosen = chosen_locks("src/Lib/Lib.csproj", "src/Lib/packages.lock.json")
        self.assertEqual(imprecise([graph], [lock], chosen, chosen), [])
        for mismatch in (
            Change(
                "added", "NuGet", "Npgsql", "10.0.4", "src/Other/packages.lock.json"
            ),
            Change("removed", "NuGet", "Npgsql", "10.0.4", lock.manifest),
            Change("added", "NuGet", "Other", "10.0.4", lock.manifest),
            Change("added", "NuGet", "Npgsql", ">= 0", lock.manifest),
        ):
            with self.subTest(mismatch=mismatch):
                self.assertEqual(
                    imprecise([graph], [mismatch], chosen, chosen), ["npgsql >= 0"]
                )

        different_range = Change("added", "nuget", "npgsql", ">= 10", graph.manifest)
        python_range = Change("added", "pip", "httpx", ">= 0", "requirements.txt")
        self.assertEqual(
            imprecise([different_range, python_range], [lock], chosen, chosen),
            ["npgsql >= 10", "httpx >= 0"],
        )

    def test_named_lock_file_is_the_only_proof_for_its_project(self) -> None:
        graph = Change("added", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        named = Change(
            "added", "NuGet", "Npgsql", "10.0.4", "src/Lib/packages.Lib.lock.json"
        )
        plain = Change(
            "added", "NuGet", "Npgsql", "10.0.4", "src/Lib/packages.lock.json"
        )
        chosen = chosen_locks("src/Lib/Lib.csproj", "src/Lib/packages.Lib.lock.json")
        self.assertEqual(imprecise([graph], [named], chosen, chosen), [])
        self.assertEqual(imprecise([graph], [plain], chosen, chosen), ["npgsql >= 0"])

    def test_lock_choice_is_taken_from_the_revision_of_the_change(self) -> None:
        graph = Change("removed", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        lock = Change(
            "removed", "NuGet", "Npgsql", "10.0.3", "src/Lib/packages.lock.json"
        )
        base_locks = chosen_locks("src/Lib/Lib.csproj", "src/Lib/packages.lock.json")
        head_locks = chosen_locks(
            "src/Lib/Lib.csproj", "src/Lib/packages.Lib.lock.json"
        )
        self.assertEqual(imprecise([graph], [lock], base_locks, head_locks), [])
        self.assertEqual(
            imprecise([graph], [lock], head_locks, base_locks), ["npgsql >= 0"]
        )
        added = Change("added", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        head_lock = Change(
            "added", "NuGet", "Npgsql", "10.0.4", "src/Lib/packages.Lib.lock.json"
        )
        self.assertEqual(imprecise([added], [head_lock], base_locks, head_locks), [])
        self.assertEqual(
            imprecise([added], [head_lock], head_locks, base_locks), ["npgsql >= 0"]
        )

    def test_shared_lock_without_proven_owner_is_not_a_proof(self) -> None:
        graph = Change("added", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        lock = Change(
            "added", "NuGet", "Npgsql", "10.0.4", "src/Lib/packages.lock.json"
        )
        shared = chosen_locks("src/Lib/Lib.csproj", None)
        self.assertEqual(
            imprecise([graph], [lock], shared, shared),
            ["npgsql >= 0"],
        )

    def test_report_codes_are_distinct(self) -> None:
        self.assertNotEqual(CHECK_FINDINGS_CODE, CHECK_ERROR_CODE)
        self.assertNotEqual(CHECK_ERROR_CODE, 0)

    def test_nuget_interval_is_not_mistaken_for_exact_version(self) -> None:
        for version in ("[10.0,11.0)", "(10.0,11.0]", "[10.0]"):
            with self.subTest(version=version):
                interval = Change(
                    "added", "NuGet", "Npgsql", version, "packages.lock.json"
                )
                self.assertEqual(
                    imprecise([interval], [], {}, {}), [f"Npgsql {version}"]
                )

    def test_empty_lock_version_keeps_the_graph_gap(self) -> None:
        graph = Change("added", "nuget", "npgsql", ">= 0", "src/Lib/Lib.csproj")
        chosen = chosen_locks("src/Lib/Lib.csproj", "src/Lib/packages.lock.json")
        empty = Change("added", "NuGet", "Npgsql", "", "src/Lib/packages.lock.json")
        self.assertEqual(imprecise([graph], [empty], chosen, chosen), ["npgsql >= 0"])
        spaced = Change("added", "NuGet", "Npgsql", "  ", empty.manifest)
        self.assertEqual(imprecise([graph], [spaced], chosen, chosen), ["npgsql >= 0"])
        resolved = Change("added", "NuGet", "Npgsql", "10.0.4", empty.manifest)
        self.assertEqual(imprecise([graph], [resolved], chosen, chosen), [])

    def test_exact_version_rejects_ranges_and_empty_values(self) -> None:
        self.assertTrue(exact_version("10.0.4"))
        for value in ("", "   ", ">= 0", "[10.0,11.0)", "(10.0,11.0]"):
            with self.subTest(value=value):
                self.assertFalse(exact_version(value))

    def test_exact_version_rejects_text_that_is_not_a_version(self) -> None:
        for value in (
            "not-a-version",
            "10",
            "v10.0.4",
            "10.0.4.5.6",
            "1.2.3-preview..1",
            "1.2.3+build..1",
        ):
            with self.subTest(value=value):
                self.assertFalse(exact_version(value))
        for value in (
            "10.0.4",
            " 10.0.4 ",
            "1.2.3.4",
            "6.0.0-preview.1",
            "1.0.0+build",
        ):
            with self.subTest(value=value):
                self.assertTrue(exact_version(value))


class NugetAuditTests(RepositoryCase):
    def test_absent_workflow_blocks_unconfirmed_nuget(self) -> None:
        with self.assertRaisesRegex(CoverageError, "не удалось прочитать"):
            _ = nuget_audit_proof(self.root)

    def test_missing_pull_request_trigger_blocks_unconfirmed_nuget(self) -> None:
        self.write(
            NUGET_AUDIT_WORKFLOW.as_posix(),
            "on:\n  push:\n    branches: [main]\n"
            "steps:\n"
            "  - run: python .github/scripts/check_nuget_audit.py --root .\n"
            "  - run: python .github/scripts/check_osv.py --root .\n",
        )
        with self.assertRaisesRegex(CoverageError, "не запускается на pull_request"):
            _ = nuget_audit_proof(self.root)

    def test_missing_script_blocks_unconfirmed_nuget(self) -> None:
        self.write(
            NUGET_AUDIT_WORKFLOW.as_posix(),
            "on:\n  pull_request:\n"
            "steps:\n  - run: python .github/scripts/check_osv.py --root .\n",
        )
        with self.assertRaisesRegex(CoverageError, "check_nuget_audit.py"):
            _ = nuget_audit_proof(self.root)

    def test_both_scripts_prove_nuget_audit(self) -> None:
        self.write(
            NUGET_AUDIT_WORKFLOW.as_posix(),
            "on:\n  pull_request:\n"
            "steps:\n"
            "  - run: python .github/scripts/check_nuget_audit.py --root .\n"
            "  - run: python .github/scripts/check_osv.py --root .\n",
        )
        self.assertEqual(
            nuget_audit_proof(self.root),
            "точные версии NuGet проверяет задание аудита зависимостей "
            "на pull_request: check_nuget_audit.py, check_osv.py",
        )
