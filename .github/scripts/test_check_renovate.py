"""Регрессии проверки Renovate: разбор отчётов, ожидания манифестов и ветки.

Движок Renovate в этих тестах не требуется: события лога, манифесты и фикстуры
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
    Arguments,
    CoverageError,
    Declaration,
    Scenario,
    branches_by_dep,
    build_parser,
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
    verify_extraction,
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
        self.assertIn(
            Declaration("nuget", "Directory.Packages.props", "Npgsql", "10.0.3"), index
        )
        self.assertEqual(
            Declaration(
                "pip_requirements",
                ".github/scripts/requirements.txt",
                "ruff",
                "==0.17.5",
            )
            in index,
            True,
        )


class ManifestTests(RepositoryCase):
    @override
    def setUp(self) -> None:
        super().setUp()
        _ = subprocess.run(  # noqa: S603
            ["git", "init", "--quiet"],  # noqa: S607
            cwd=self.root,
            check=True,
            capture_output=True,
        )

    @override
    def write(self, name: str, text: str) -> Path:
        path = super().write(name, text)
        _ = subprocess.run(  # noqa: S603
            ["git", "add", "--", name],  # noqa: S607
            cwd=self.root,
            check=True,
            capture_output=True,
        )
        return path

    def extraction(
        self, manager: str, declarations: dict[str, list[dict[str, str]]]
    ) -> str:
        paths = list(declarations)
        patterns = {
            "nuget": r"/\.(?:csproj|props|targets)$|(?:^|/)(?:global|dotnet-tools)\.json$/",
            "pip_requirements": r"/(^|/)[\w-]*requirements([-._]\w+)?\.(txt|pip)$/",
            "dockerfile": r"/(^|/)Dockerfile$/",
            "docker-compose": r"/(^|/)compose[^/]*\.ya?ml$/",
            "github-actions": r"/^\.github/workflows/[^/]+\.ya?ml$/",
        }
        discovery = [
            json.dumps({"msg": f"Using file pattern: {pattern} for manager {name}"})
            for name, pattern in patterns.items()
        ]
        return (
            "\n".join(discovery)
            + "\n"
            + "\n".join(
                json.dumps(item)
                for item in (
                    {
                        "msg": f"Matched {len(paths)} file(s) for manager {manager}: {', '.join(paths)}"
                    },
                    {
                        "msg": "Extracted dependencies",
                        "packageFiles": {
                            manager: [
                                {"packageFile": path, "deps": deps}
                                for path, deps in declarations.items()
                            ]
                        },
                    },
                )
            )
        )

    def test_new_requirements_manifest_and_each_version_must_be_extracted(self) -> None:
        for path, version in (
            ("requirements.txt", "1.0"),
            ("nested/dev-requirements.pip", "2.0"),
        ):
            _ = self.write(path, f"package=={version}\n")
        declarations = {
            "requirements.txt": [{"depName": "package", "currentValue": "==1.0"}],
            "nested/dev-requirements.pip": [
                {"depName": "package", "currentValue": "==2.0"}
            ],
        }
        self.assertEqual(
            len(
                verify_extraction(
                    self.root, self.extraction("pip_requirements", declarations)
                )
            ),
            2,
        )
        for entries in (
            [],
            [{"depName": "package", "currentValue": "==1.0"}],
            [{"depName": "package", "currentValue": "==2"}],
        ):
            broken = {**declarations, "nested/dev-requirements.pip": entries}
            with self.subTest(entries=entries), self.assertRaises(CoverageError):
                _ = verify_extraction(
                    self.root, self.extraction("pip_requirements", broken)
                )

    def test_git_inventory_catches_new_file_missing_from_detection(self) -> None:
        _ = self.write("requirements.txt", "package==1.0\n")
        _ = self.write("nested/dev-requirements.pip", "package==2.0\n")
        output = self.extraction(
            "pip_requirements",
            {"requirements.txt": [{"depName": "package", "currentValue": "==1.0"}]},
        )
        with self.assertRaisesRegex(CoverageError, "деревом Git"):
            _ = verify_extraction(self.root, output)

    def test_nuget_all_detected_paths_preserve_versions(self) -> None:
        paths = [
            "nested/Directory.Packages.props",
            "nested/global.json",
            "nested/dotnet-tools.json",
            "new/app.csproj",
        ]
        _ = self.write(
            paths[0],
            '<Project><ItemGroup><PackageVersion Include="Npgsql" Version="10.0.3" /></ItemGroup></Project>',
        )
        _ = self.write(paths[1], '{"sdk":{"version":"10.0.402"}}')
        _ = self.write(paths[2], '{"tools":{"dotnet-ef":{"version":"10.0.14"}}}')
        _ = self.write(
            paths[3],
            '<Project><ItemGroup><PackageReference Version="10.0.4" Include="Npgsql" /></ItemGroup></Project>',
        )
        expected = expected_nuget(self.root, paths)
        self.assertEqual(
            expected,
            {
                Declaration("nuget", paths[0], "Npgsql", "10.0.3"),
                Declaration("nuget", paths[1], "dotnet-sdk", "10.0.402"),
                Declaration("nuget", paths[2], "dotnet-ef", "10.0.14"),
                Declaration("nuget", paths[3], "Npgsql", "10.0.4"),
            },
        )
        with self.assertRaises(CoverageError):
            _ = verify_expected(
                expected,
                expected - {Declaration("nuget", paths[3], "Npgsql", "10.0.4")},
                "nuget",
            )

    def test_namespaced_nuget_and_node_runtime_keep_declared_versions(self) -> None:
        _ = self.write(
            "nested/legacy.csproj",
            '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup><PackageReference Include="Package" Version="1"><VersionOverride>2</VersionOverride></PackageReference></ItemGroup></Project>',
        )
        expected = expected_nuget(self.root, ["nested/legacy.csproj"])
        self.assertEqual(
            expected, {Declaration("nuget", "nested/legacy.csproj", "Package", "2")}
        )
        _ = self.write(
            ".github/workflows/node.yml",
            "steps:\n  - uses: actions/setup-node@v6\n    with:\n      node-version: '24'\n",
        )
        expected = expected_actions(self.root, [".github/workflows/node.yml"])
        runtime = Declaration(
            "github-actions", ".github/workflows/node.yml", "node", "24"
        )
        self.assertIn(runtime, expected)
        with self.assertRaises(CoverageError):
            _ = verify_expected(expected, expected - {runtime}, "actions")
        _ = self.write(
            ".github/workflows/node.yml",
            "steps:\n  - uses: actions/setup-node@v6\n    with:\n      node-version-file: .node-version\n",
        )
        with self.assertRaises(CoverageError):
            _ = expected_actions(self.root, [".github/workflows/node.yml"])

    def test_selected_config_is_used_for_custom_manager_coverage(self) -> None:
        _ = self.write("requirements.txt", "package==1.0\n")
        _ = self.write("renovate.json", '{"customManagers":[{}]}')
        selected = self.write("alternative.json", "{}")
        output = self.extraction(
            "pip_requirements",
            {"requirements.txt": [{"depName": "package", "currentValue": "==1.0"}]},
        )
        self.assertEqual(
            len(verify_extraction(self.root, output, Path("alternative.json"))), 1
        )
        with self.assertRaises(CoverageError):
            _ = verify_extraction(self.root, output)
        _ = self.write("renovate.json", "{}")
        _ = self.write("alternative.json", '{"customManagers":[{}]}')
        with self.assertRaises(CoverageError):
            _ = verify_extraction(self.root, output, selected)

    def test_each_dockerfile_compose_and_workflow_preserves_identity(self) -> None:
        _ = self.write(
            "nested/Dockerfile",
            "ARG SDK=registry:5000/sdk:1@sha256:abc\nFROM ${SDK} AS build\nFROM build AS final\nFROM postgres:18\n",
        )
        _ = self.write(
            "nested/compose.yml",
            "services:\n  db:\n    image: postgres:19\n  local:\n    image: local:tag\n    build: .\n",
        )
        _ = self.write(
            ".github/workflows/new.yml",
            "steps:\n  - uses: actions/checkout@" + "a" * 40 + " # v7.0.1\n",
        )
        expected = expected_docker(
            self.root,
            {
                "dockerfile": ["nested/Dockerfile"],
                "docker-compose": ["nested/compose.yml"],
            },
        )
        self.assertEqual(
            expected,
            {
                Declaration(
                    "dockerfile",
                    "nested/Dockerfile",
                    "registry:5000/sdk",
                    "1",
                    "sha256:abc",
                ),
                Declaration("dockerfile", "nested/Dockerfile", "postgres", "18"),
                Declaration("docker-compose", "nested/compose.yml", "postgres", "19"),
            },
        )
        action = Declaration(
            "github-actions",
            ".github/workflows/new.yml",
            "actions/checkout",
            "v7.0.1",
            "a" * 40,
        )
        self.assertEqual(expected_actions(self.root, [action.path]), {action})
        for broken in (
            Declaration(
                action.manager, action.path, action.name, action.version, "b" * 40
            ),
            Declaration(
                action.manager,
                ".github/workflows/other.yml",
                action.name,
                action.version,
                action.digest,
            ),
        ):
            with self.subTest(broken=broken), self.assertRaises(CoverageError):
                _ = verify_expected({action}, {broken}, "actions")

    def test_quoted_action_reference_keeps_identity(self) -> None:
        path = ".github/workflows/quoted.yml"
        sha = "a" * 40
        _ = self.write(
            path,
            f"steps:\n  - uses: \"actions/checkout@{sha}\" # v7.0.1\n  - uses: 'actions/setup-node@v7'\n",
        )
        self.assertEqual(
            expected_actions(self.root, [path]),
            {
                Declaration("github-actions", path, "actions/checkout", "v7.0.1", sha),
                Declaration("github-actions", path, "actions/setup-node", "v7"),
            },
        )

    def test_commented_action_reference_is_not_a_declaration(self) -> None:
        path = ".github/workflows/comments.yml"
        sha = "a" * 40
        _ = self.write(
            path,
            f"jobs:\n  test:\n    steps:\n      # - uses: actions/checkout@v6\n      - uses: actions/checkout@{sha} # v7.0.1\n",
        )
        self.assertEqual(
            expected_actions(self.root, [path]),
            {Declaration("github-actions", path, "actions/checkout", "v7.0.1", sha)},
        )

    def test_docker_action_is_verified_as_image(self) -> None:
        path = ".github/workflows/docker-action.yml"
        _ = self.write(
            path,
            "jobs:\n  test:\n    steps:\n      - uses: docker://alpine:3.22\n",
        )
        self.assertEqual(
            expected_actions(self.root, [path]),
            {Declaration("github-actions", path, "alpine", "3.22")},
        )

    def test_msbuild_sdk_declaration_is_verified(self) -> None:
        path = "src/App/App.csproj"
        _ = self.write(
            path,
            '<Project Sdk="MSBuild.Sdk.Extras/3.0.44"><ItemGroup><PackageReference Include="Npgsql" Version="10.0.4" /></ItemGroup></Project>',
        )
        self.assertEqual(
            expected_nuget(self.root, [path]),
            {
                Declaration("nuget", path, "MSBuild.Sdk.Extras", "3.0.44"),
                Declaration("nuget", path, "Npgsql", "10.0.4"),
            },
        )

    def test_copy_external_images_require_exact_extraction(self) -> None:
        path = "Dockerfile"
        _ = self.write(
            path,
            "FROM alpine:3.22 AS build\nCOPY --from=build /a /a\nCOPY --from=0 /b /b\nCOPY --from=busybox:1.37@sha256:abc /bin/busybox /bin/busybox\n",
        )
        deps = [
            {"depName": "alpine", "currentValue": "3.22"},
            {
                "depName": "busybox",
                "currentValue": "1.37",
                "currentDigest": "sha256:abc",
            },
        ]
        self.assertEqual(
            len(
                verify_extraction(
                    self.root, self.extraction("dockerfile", {path: deps})
                )
            ),
            2,
        )
        for broken in (
            deps[:1],
            [deps[0], {**deps[1], "currentDigest": "sha256:wrong"}],
        ):
            with self.subTest(broken=broken), self.assertRaises(CoverageError):
                _ = verify_extraction(
                    self.root, self.extraction("dockerfile", {path: broken})
                )

    def test_actions_subpaths_are_verified_using_replace_string(self) -> None:
        path = ".github/workflows/test.yml"
        _ = self.write(
            path,
            "jobs:\n  test:\n    steps:\n      - uses: github/codeql-action/init@v4\n      - uses: org/repo/.github/workflows/check.yml@v2\n",
        )
        deps = [
            {
                "depName": "github/codeql-action",
                "currentValue": "v4",
                "depType": "action",
                "replaceString": "github/codeql-action/init@v4",
            },
            {
                "depName": "org/repo",
                "currentValue": "v2",
                "depType": "workflow",
                "replaceString": "org/repo/.github/workflows/check.yml@v2",
            },
        ]
        self.assertEqual(
            len(
                verify_extraction(
                    self.root, self.extraction("github-actions", {path: deps})
                )
            ),
            2,
        )
        for wrong in ("github/codeql-action/analyze@v4", "github/codeql-action@v4"):
            broken = [{**deps[0], "replaceString": wrong}, deps[1]]
            with self.subTest(wrong=wrong), self.assertRaises(CoverageError):
                _ = verify_extraction(
                    self.root, self.extraction("github-actions", {path: broken})
                )

    def test_workflow_container_and_service_images_require_exact_extraction(
        self,
    ) -> None:
        path = ".github/workflows/test.yml"
        _ = self.write(
            path,
            "jobs:\n  test:\n    container: node:24\n    services:\n      db:\n        image: postgres:18@sha256:abc\n",
        )
        deps = [
            {"depName": "node", "currentValue": "24"},
            {
                "depName": "postgres",
                "currentValue": "18",
                "currentDigest": "sha256:abc",
            },
        ]
        self.assertEqual(
            len(
                verify_extraction(
                    self.root, self.extraction("github-actions", {path: deps})
                )
            ),
            2,
        )
        for broken in (
            deps[:1],
            [deps[0], {**deps[1], "currentDigest": "sha256:wrong"}],
        ):
            with self.subTest(broken=broken), self.assertRaises(CoverageError):
                _ = verify_extraction(
                    self.root, self.extraction("github-actions", {path: broken})
                )
        _ = self.write(path, "jobs:\n  test:\n    container: ${{ matrix.image }}\n")
        with self.assertRaises(CoverageError):
            _ = expected_actions(self.root, [path])

    def test_missing_detection_and_unknown_manager_do_not_claim_coverage(self) -> None:
        with self.assertRaises(CoverageError):
            _ = verify_extraction(self.root, extract_event())
        with self.assertRaisesRegex(CoverageError, "Нет независимой сверки"):
            _ = verify_extraction(
                self.root, self.extraction("npm", {"new/package.json": []})
            )

    def test_expected_pip_rejects_unproved_formats(self) -> None:
        _ = self.write("requirements.txt", "package>=1\n")
        with self.assertRaises(CoverageError):
            _ = expected_pip(self.root, ["requirements.txt"])


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
            "".join(
                (
                    "import json, os, sys\nfrom pathlib import Path\n",
                    f"packages = {packages!r}\nfailure = {failure!r}\nmissing = {missing!r}\n",
                    "root = Path.cwd()\nname = root.name\n",
                    "assert os.environ['RENOVATE_BASE_DIR'] == str(root / '.renovate')\n",
                    "assert os.environ['RENOVATE_CACHE_DIR'] == str(root / '.renovate-cache')\n",
                    "if name == failure:\n    sys.exit(7)\n",
                    "files = []\n",
                    "for path, deps in packages[name].items():\n",
                    "    entries = [{'depName': dep, 'updates': [{'branchName': ",
                    "'renovate/grouped' if name == 'grouping-control' else 'renovate/' + dep}]} ",
                    "for dep in deps if dep != missing]\n",
                    "    files.append({'packageFile': path, 'deps': entries})\n",
                    "print(json.dumps({'msg': 'packageFiles with updates', "
                    + "'config': {'nuget': files}}))\n",
                )
            ),
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
            _node: str, _script: Path, directory: Path, _config_file: Path
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


class WorkerLimitTests(unittest.TestCase):
    def test_cli_and_library_reject_more_than_two_workers(self) -> None:
        for count in (0, 3, -1):
            with self.subTest(count=count):
                with self.assertRaises(SystemExit):
                    _ = build_parser().parse_args(["fixtures", "--workers", str(count)])
                with self.assertRaises(CoverageError):
                    _ = run_fixtures("", Path("unused"), Path("unused"), workers=count)
        for count in (1, 2):
            self.assertEqual(
                build_parser()
                .parse_args(
                    ["fixtures", "--workers", str(count)], namespace=Arguments()
                )
                .workers,
                count,
            )


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
