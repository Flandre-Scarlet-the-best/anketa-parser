"""Регрессии проверки Renovate: разбор отчётов, ожидания manifests и ветки.

Движок Renovate в этих тестах не требуется: события лога, manifests и фикстуры
создаются в временном каталоге, поэтому проверяются решения проверки — пропущенная
зависимость, расхождение версии, объединённая ветка и сбой инструмента не могут
дать ложный успех.
"""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from threading import Barrier, Lock
from typing import cast, override
from unittest.mock import patch

from check_renovate import (
    NEGATIVE,
    SCENARIOS,
    CoverageError,
    Scenario,
    branches_by_dep,
    expected_actions,
    expected_docker,
    expected_nuget,
    expected_pip,
    extraction_index,
    grouped_config,
    iter_package_files,
    run_fixtures,
    run_script,
    updates,
    verify_expected,
    verify_relation,
)

FAKE = """import sys
print('{"msg":"fake"}')
print("диагностика", file=sys.stderr)
sys.exit(int(sys.argv[1]))
"""


def extract_event() -> str:
    """Событие режима extract с разделом packageFiles."""
    payload: dict[str, object] = {
        "msg": "Extracted dependencies",
        "packageFiles": {
            "nuget": [
                {
                    "packageFile": "Directory.Packages.props",
                    "deps": [
                        {"depName": "Npgsql", "currentValue": "10.0.3", "updates": []},
                        {
                            "depName": "dotnet-sdk",
                            "currentValue": "10.0.402",
                            "updates": [],
                        },
                    ],
                }
            ],
            "pip_requirements": [
                {
                    "packageFile": ".github/scripts/requirements.txt",
                    "deps": [
                        {"depName": "ruff", "currentValue": "==0.17.5", "updates": []}
                    ],
                }
            ],
        },
    }
    return json.dumps(payload)


def lookup_event() -> str:
    """Событие режима lookup с разрешённой конфигурацией."""
    payload: dict[str, object] = {
        "msg": "packageFiles with updates",
        "config": {
            "prConcurrentLimit": 4,
            "packageRules": [{"matchManagers": ["nuget"], "groupName": "x"}],
            "nuget": [
                {
                    "packageFile": "app.csproj",
                    "deps": [
                        {
                            "depName": "Serilog",
                            "currentValue": "3.0.0",
                            "updates": [
                                {"branchName": "renovate/serilog-3.x"},
                                {"branchName": "renovate/serilog-3.x"},
                            ],
                        },
                        {
                            "depName": "Polly",
                            "currentValue": "8.0.0",
                            "updates": [{"branchName": "renovate/polly-8.x"}],
                        },
                    ],
                },
                {
                    "packageFile": "second/second.csproj",
                    "deps": [
                        {
                            "depName": "Serilog",
                            "currentValue": "3.0.0",
                            "updates": [{"branchName": "renovate/serilog-3.x"}],
                        }
                    ],
                },
            ],
        },
    }
    return json.dumps(payload)


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

    def write(self, name: str, text: str) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")
        return path


class EventParsingTests(unittest.TestCase):
    def test_extract_and_lookup_events_expose_package_files(self) -> None:
        entries = iter_package_files(extract_event() + "\n" + lookup_event())
        paths = [path for _, path, _ in entries]
        self.assertIn("Directory.Packages.props", paths)
        self.assertIn(".github/scripts/requirements.txt", paths)
        self.assertIn("app.csproj", paths)

    def test_non_json_and_unrelated_events_are_ignored(self) -> None:
        self.assertEqual(
            iter_package_files("текст\n{}\n" + json.dumps({"msg": "другое"})), []
        )

    def test_branches_are_collected_per_dependency_and_deduplicated(self) -> None:
        found = updates(lookup_event())
        self.assertEqual(found[("app.csproj", "Serilog")], ("renovate/serilog-3.x",))
        self.assertEqual(found[("app.csproj", "Polly")], ("renovate/polly-8.x",))
        self.assertEqual(
            branches_by_dep(found),
            {"Serilog": {"renovate/serilog-3.x"}, "Polly": {"renovate/polly-8.x"}},
        )

    def test_extraction_index_keeps_values_per_file(self) -> None:
        index = extraction_index(extract_event())
        self.assertEqual(index["Npgsql"], [("Directory.Packages.props", "10.0.3")])
        self.assertEqual(
            index["ruff"], [(".github/scripts/requirements.txt", "==0.17.5")]
        )


class ManifestTests(RepositoryCase):
    def test_expected_versions_come_from_manifests(self) -> None:
        _ = self.write(
            "Directory.Packages.props",
            '<Project><ItemGroup>\n  <PackageVersion Include="Npgsql" Version="10.0.3" />\n</ItemGroup></Project>',
        )
        _ = self.write("global.json", json.dumps({"sdk": {"version": "10.0.402"}}))
        _ = self.write(
            ".config/dotnet-tools.json",
            json.dumps({"tools": {"dotnet-ef": {"version": "10.0.14"}}}),
        )
        self.assertEqual(
            expected_nuget(self.root),
            {"Npgsql": "10.0.3", "dotnet-sdk": "10.0.402", "dotnet-ef": "10.0.14"},
        )

    def test_pip_docker_and_actions_expectations(self) -> None:
        _ = self.write(
            ".github/scripts/requirements.txt", "# комментарий\nruff==0.17.5\n"
        )
        _ = self.write(
            "Dockerfile",
            "ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-noble\nFROM ${SDK_IMAGE} AS build\nFROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble\n",
        )
        _ = self.write(
            "compose.yaml",
            "services:\n  db:\n    image: postgres:18.6-alpine\n  host:\n    image: anketa-parser-host:local\n",
        )
        _ = self.write(
            ".github/workflows/ci.yml",
            "steps:\n  - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n    with:\n      python-version: '3.15.2'\n",
        )
        self.assertEqual(expected_pip(self.root), {"ruff": "0.17.5"})
        self.assertEqual(
            expected_docker(self.root),
            {
                "mcr.microsoft.com/dotnet/runtime": "10.0.14-noble",
                "mcr.microsoft.com/dotnet/sdk": "10.0.402-noble",
                "postgres": "18.6-alpine",
            },
        )
        self.assertEqual(
            expected_actions(self.root),
            {"actions/checkout": "v7.0.1", "python": "3.15.2"},
        )

    def test_missing_or_mismatched_extraction_is_error(self) -> None:
        index = {"Npgsql": [("Directory.Packages.props", "10.0.3")]}
        self.assertEqual(
            verify_expected({"Npgsql": "10.0.3"}, index, "nuget"),
            ["nuget Npgsql 10.0.3 из 1 файл(ов)"],
        )
        with self.assertRaises(CoverageError):
            _ = verify_expected(
                {"Npgsql": "10.0.3", "Serilog": "3.0.0"}, index, "nuget"
            )
        with self.assertRaises(CoverageError):
            _ = verify_expected({"Npgsql": "11.0.0"}, index, "nuget")
        self.assertEqual(
            verify_expected({"ruff": "0.17.5"}, {"ruff": [("req", "==0.17.5")]}, "pip"),
            ["pip ruff 0.17.5 из 1 файл(ов)"],
        )


class RelationTests(unittest.TestCase):
    def test_distinct_branches_pass_and_grouping_fails(self) -> None:
        scenario = Scenario("s", {}, ("Serilog", "Polly"), "distinct")
        verify_relation(
            {
                ("a.csproj", "Serilog"): ("renovate/serilog-3.x",),
                ("a.csproj", "Polly"): ("renovate/polly-8.x",),
            },
            scenario,
        )
        with self.assertRaises(CoverageError):
            verify_relation(
                {
                    ("a.csproj", "Serilog"): ("renovate/grouped",),
                    ("a.csproj", "Polly"): ("renovate/grouped",),
                },
                scenario,
            )

    def test_absent_updates_and_single_file_are_errors(self) -> None:
        scenario = Scenario("s", {}, ("Serilog", "Polly"), "distinct")
        with self.assertRaises(CoverageError):
            verify_relation(
                {("a.csproj", "Serilog"): ("renovate/serilog-3.x",)}, scenario
            )
        single = Scenario("s", {}, ("Serilog",), "single")
        with self.assertRaises(CoverageError):
            verify_relation(
                {("a.csproj", "Serilog"): ("renovate/serilog-3.x",)}, single
            )

    def test_same_dependency_in_two_files_must_share_branch(self) -> None:
        single = Scenario("s", {}, ("Serilog",), "single")
        verify_relation(
            {
                ("a.csproj", "Serilog"): ("renovate/serilog-3.x",),
                ("b.csproj", "Serilog"): ("renovate/serilog-3.x",),
            },
            single,
        )
        with self.assertRaises(CoverageError):
            verify_relation(
                {
                    ("a.csproj", "Serilog"): ("renovate/serilog-3.x",),
                    ("b.csproj", "Serilog"): ("renovate/serilog-4.x",),
                },
                single,
            )


class ConfigTests(RepositoryCase):
    def test_grouped_config_injects_rule_and_clears_ignored_presets(self) -> None:
        config = self.write(
            "renovate.json",
            json.dumps({"ignorePresets": ["group:monorepos"], "packageRules": []}),
        )
        target = self.root / "grouped.json"
        result = grouped_config(config, target)
        data = cast(dict[str, object], json.loads(result.read_text(encoding="utf-8")))
        self.assertEqual(data["ignorePresets"], [])
        rules = cast(list[object], data["packageRules"])
        self.assertEqual(
            cast(dict[str, object], rules[0])["groupName"], "grouped-updates"
        )

    def test_invalid_config_is_rejected(self) -> None:
        broken = self.write("broken.json", "{")
        with self.assertRaises(CoverageError):
            _ = grouped_config(broken, self.root / "out.json")
        without_rules = self.write("rules.json", json.dumps({"ignorePresets": []}))
        with self.assertRaises(CoverageError):
            _ = grouped_config(without_rules, self.root / "out.json")


class FixtureRunnerTests(RepositoryCase):
    def config(self) -> Path:
        return self.write("renovate.json", '{"packageRules": []}')

    def fake_engine(self, failure: str = "", missing: str = "") -> Path:
        """Подпроцесс выдаёт реальные формы lookup-лога для каждой фикстуры."""
        scenarios = {scenario.name: scenario for scenario in (*SCENARIOS, NEGATIVE)}
        packages = {
            name: {
                path: [dep for dep in scenario.deps if dep in content]
                for path, content in scenario.files.items()
            }
            for name, scenario in scenarios.items()
        }
        return self.write(
            "engine.py",
            "import json, os, sys\nfrom pathlib import Path\n"
            f"packages = {packages!r}\nfailure = {failure!r}\nmissing = {missing!r}\n"
            "root = Path.cwd()\nname = root.name\n"
            "assert os.environ['RENOVATE_BASE_DIR'] == str(root / '.renovate')\n"
            "assert os.environ['RENOVATE_CACHE_DIR'] == str(root / '.renovate-cache')\n"
            "if name == failure:\n    sys.exit(7)\n"
            "files = []\n"
            "for path, deps in packages[name].items():\n"
            "    entries = [{'depName': dep, 'updates': [{'branchName': "
            "'renovate/grouped' if name == 'grouping-control' else 'renovate/' + dep}]} "
            "for dep in deps if dep != missing]\n"
            "    files.append({'packageFile': path, 'deps': entries})\n"
            "print(json.dumps({'msg': 'packageFiles with updates', 'config': {'nuget': files}}))\n",
        )

    def test_parallel_subprocesses_verify_all_scenarios_in_stable_order(self) -> None:
        config = self.config()
        engine = self.fake_engine()
        serial = run_fixtures(sys.executable, engine, config, workers=1)
        parallel = run_fixtures(sys.executable, engine, config, workers=2)
        self.assertEqual(parallel, serial)
        self.assertEqual(
            [note.split(":", 1)[0] for note in parallel],
            [scenario.name for scenario in (*SCENARIOS, NEGATIVE)],
        )
        self.assertIn("объединены", parallel[-1])

    def test_failure_of_each_subprocess_rejects_entire_fixture_run(self) -> None:
        config = self.config()
        for scenario in (*SCENARIOS, NEGATIVE):
            with self.subTest(scenario=scenario.name):
                engine = self.fake_engine(failure=scenario.name)
                with self.assertRaisesRegex(CoverageError, "кодом 7"):
                    _ = run_fixtures(sys.executable, engine, config)

    def test_cli_failure_does_not_print_partial_pass(self) -> None:
        result = subprocess.run(  # noqa: S603
            [
                sys.executable,
                str(Path(__file__).with_name("check_renovate.py")),
                "fixtures",
                "--root",
                str(self.root),
                "--node",
                sys.executable,
                "--renovate",
                str(self.fake_engine(failure=NEGATIVE.name)),
                "--config",
                str(self.config()),
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("кодом 7", result.stderr)
        self.assertNotIn("Renovate fixtures:", result.stdout)

    def test_missing_updates_reject_positive_and_negative_control(self) -> None:
        config = self.config()
        for dependency in ("Polly", "Autofac"):
            with self.subTest(dependency=dependency):
                engine = self.fake_engine(missing=dependency)
                with self.assertRaises(CoverageError):
                    _ = run_fixtures(sys.executable, engine, config)

    def test_negative_control_without_grouping_rejects_run(self) -> None:
        engine = self.fake_engine()
        _ = engine.write_text(
            engine.read_text(encoding="utf-8").replace(
                "name == 'grouping-control'", "False"
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(CoverageError, "не дал группировки"):
            _ = run_fixtures(sys.executable, engine, self.config())

    def test_workers_overlap_without_exceeding_limit_or_sharing_directories(
        self,
    ) -> None:
        barrier = Barrier(2, timeout=10)
        lock = Lock()
        active = 0
        peak = 0
        directories: list[Path] = []

        def fake_lookup(
            node: str, script: Path, directory: Path, config_file: Path
        ) -> dict[tuple[str, str], tuple[str, ...]]:
            nonlocal active, peak
            with lock:
                active += 1
                peak = max(peak, active)
                directories.append(directory)
                number = len(directories)
            if number <= 2:
                _ = barrier.wait()
            scenario = next(
                item for item in (*SCENARIOS, NEGATIVE) if item.name == directory.name
            )
            result = {
                (path, dep): (
                    "renovate/grouped" if scenario == NEGATIVE else f"renovate/{dep}",
                )
                for path, content in scenario.files.items()
                for dep in scenario.deps
                if dep in content
            }
            with lock:
                active -= 1
            return result

        with patch("check_renovate.lookup", side_effect=fake_lookup):
            notes = run_fixtures("", Path("engine"), self.config(), workers=2)
        self.assertEqual(len(notes), len(SCENARIOS) + 1)
        self.assertEqual(peak, 2)
        self.assertEqual(len(set(directories)), len(notes))
        self.assertTrue(all(not path.exists() for path in directories))


class RunnerTests(RepositoryCase):
    def test_successful_script_returns_output(self) -> None:
        script = self.write("fake.py", FAKE)
        output = run_script([sys.executable, str(script), "0"], self.root)
        self.assertIn('"msg":"fake"', output)
        self.assertIn("диагностика", output)

    def test_failing_or_missing_script_is_coverage_error(self) -> None:
        script = self.write("fake.py", FAKE)
        with self.assertRaises(CoverageError):
            _ = run_script([sys.executable, str(script), "3"], self.root)
        with self.assertRaises(CoverageError):
            _ = run_script([str(self.root / "absent")], self.root)

    def test_environment_is_passed_to_the_script(self) -> None:
        script = self.write(
            "env.py",
            "import os\nprint(os.environ.get('RENOVATE_PLATFORM', 'нет'))\n",
        )
        output = run_script(
            [sys.executable, str(script)], self.root, {"RENOVATE_PLATFORM": "local"}
        )
        self.assertIn("local", output)

    def test_subprocess_is_used_without_shell(self) -> None:
        result = subprocess.run(  # noqa: S603
            [sys.executable, "-c", "print('ok')"],  # noqa: S607
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertIn("ok", result.stdout)
