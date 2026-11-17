"""Регрессии CI: точное подтверждение, отсутствие отметки после ошибки и один анализ типов."""

import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import cast

import yaml

ROOT = Path(__file__).resolve().parents[2]
FULL_CHECK = (
    "steps.proof.outputs.cache-hit != 'true' || "
    "github.event_name == 'workflow_dispatch'"
)


def load_workflow(name: str) -> dict[str, object]:
    # BaseLoader не создаёт объекты Python и не превращает ключ on в bool.
    return cast(
        dict[str, object],
        yaml.load(
            (ROOT / ".github/workflows" / name).read_text(encoding="utf-8"),
            Loader=yaml.BaseLoader,  # noqa: S506
        ),
    )


def job_steps(workflow: dict[str, object], job: str) -> list[dict[str, object]]:
    jobs = cast(dict[str, dict[str, object]], workflow["jobs"])
    return cast(list[dict[str, object]], jobs[job]["steps"])


def bash_command() -> str:
    if os.name == "nt":
        git = shutil.which("git")
        if git:
            bundled = Path(git).resolve().parents[1] / "bin/bash.exe"
            if bundled.is_file():
                return str(bundled)
    bash = shutil.which("bash")
    if not bash:
        raise RuntimeError("Для исполнения команд workflow требуется Bash")
    return bash


def run_bash(
    script: str, cwd: Path, env: dict[str, str]
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(  # noqa: S603
        [bash_command(), "--noprofile", "--norc", "-eo", "pipefail", "-c", script],
        cwd=cwd,
        env={**os.environ, **env},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def stub_command(name: str, script: Path) -> str:
    """Функция Bash исключает коллизии с установленными exe, в том числе в Windows."""
    python = shlex.quote(Path(sys.executable).as_posix())
    path = shlex.quote(script.as_posix())
    return f'{name}() {{ {python} {path} "$@"; }}\n'


class RenovateProofTests(unittest.TestCase):
    def test_proof_has_exact_daily_tree_key_and_all_events_cover_new_manifests(self):
        workflow = load_workflow("renovate-config.yml")
        triggers = cast(dict[str, object], workflow["on"])
        self.assertTrue({"pull_request", "push", "workflow_dispatch"} <= set(triggers))
        self.assertNotIn("paths", str(triggers))
        steps = job_steps(workflow, "renovate-config")
        inputs = next(step for step in steps if step.get("id") == "inputs")
        self.assertIn("git rev-parse 'HEAD^{tree}'", str(inputs["run"]))
        self.assertIn("date -u +%F", str(inputs["run"]))
        proof = next(step for step in steps if step.get("id") == "proof")
        settings = cast(dict[str, str], proof["with"])
        self.assertEqual(settings["lookup-only"], "true")
        self.assertNotIn("restore-keys", settings)
        for field in (
            "runner.os",
            "runner.arch",
            "inputs.outputs.tree",
            "inputs.outputs.day",
        ):
            self.assertIn(field, settings["key"])
        self.assert_proof_stages(steps)

    def assert_proof_stages(self, steps: list[dict[str, object]]) -> None:
        marker = next(step for step in steps if step.get("id") == "verified")
        full = [
            step
            for step in steps
            if step.get("if") == FULL_CHECK and step is not marker
        ]
        for operation in (
            "--version",
            "check_renovate.py validate",
            "check_renovate.py extract",
            "check_renovate.py fixtures",
            "test_check_renovate.py",
        ):
            required = next(
                step for step in steps if operation in str(step.get("run", ""))
            )
            self.assertIn(required, full)
        self.assertTrue(full)
        for step in [*full, marker]:
            self.assertEqual(step.get("if"), FULL_CHECK)
            self.assertNotIn("continue-on-error", step)
        self.assertTrue(all(steps.index(step) < steps.index(marker) for step in full))
        self.assertIn("renovate-proof/verified", str(marker["run"]))

    def test_additional_required_stage_and_trailing_step_preserve_contract(self):
        steps = job_steps(load_workflow("renovate-config.yml"), "renovate-config")
        marker = next(step for step in steps if step.get("id") == "verified")
        steps.insert(
            steps.index(marker),
            {"name": "Дополнительная проверка", "if": FULL_CHECK, "run": "true"},
        )
        steps.append({"name": "Сообщить результат", "run": "echo завершено"})
        self.assert_proof_stages(steps)

    def test_exact_hit_tree_miss_and_manual_dispatch(self):
        steps = job_steps(load_workflow("renovate-config.yml"), "renovate-config")
        marker = next(step for step in steps if step.get("id") == "verified")
        condition = str(marker["if"])
        self.assertEqual(condition, FULL_CHECK)
        expression = condition.replace(
            "steps.proof.outputs.cache-hit", '"$CACHE_HIT"'
        ).replace("github.event_name", '"$EVENT_NAME"')
        for hit, event, expected in (
            ("true", "pull_request", False),
            ("false", "pull_request", True),
            ("", "pull_request", True),
            ("true", "workflow_dispatch", True),
        ):
            with self.subTest(hit=hit, event=event):
                result = run_bash(
                    f"if [[ {expression} ]]; then echo full; else echo cached; fi",
                    ROOT,
                    {"CACHE_HIT": hit, "EVENT_NAME": event},
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(
                    result.stdout.strip(), "full" if expected else "cached"
                )

    def test_each_stage_failure_prevents_marker_and_full_success_creates_it(self):
        steps = job_steps(load_workflow("renovate-config.yml"), "renovate-config")
        # Дополнительный обязательный шаг также участвует в отрицательных проверках.
        marker_step = next(step for step in steps if step.get("id") == "verified")
        steps.insert(
            steps.index(marker_step),
            {"if": FULL_CHECK, "run": 'test "$FAIL_STAGE" != "$CURRENT_STAGE"'},
        )
        full = [step for step in steps if step.get("if") == FULL_CHECK]
        commands = [
            f"export CURRENT_STAGE={index}\n{step['run']}"
            for index, step in enumerate(full)
        ]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docker = root / "docker"
            _ = docker.write_text(
                "#!/usr/bin/env python\nimport os, sys\n"
                "command = ' '.join(sys.argv)\n"
                "if '--version' in command:\n    print('44.251.0')\n"
                "sys.exit(7 if os.environ['CURRENT_STAGE'] == os.environ.get('FAIL_STAGE') else 0)\n",
                encoding="utf-8",
            )
            docker.chmod(0o755)
            for stage in [
                str(index)
                for index, step in enumerate(full)
                if step.get("id") != "verified"
            ] + [""]:
                with self.subTest(stage=stage or "успех"):
                    marker = root / "renovate-proof/verified"
                    marker.unlink(missing_ok=True)
                    command = stub_command("docker", docker) + "\n".join(commands)
                    result = run_bash(
                        command,
                        ROOT,
                        {
                            "RUNNER_TEMP": root.as_posix(),
                            "RENOVATE_IMAGE": "fixture",
                            "FAIL_STAGE": stage,
                        },
                    )
                    if stage:
                        self.assertNotEqual(
                            result.returncode, 0, result.stdout + result.stderr
                        )
                        self.assertFalse(marker.exists())
                    else:
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assertIn(
                            "Все обязательные проверки пройдены",
                            marker.read_text(encoding="utf-8"),
                        )

    def test_tree_input_changes_for_content_addition_deletion_and_rename(self):
        steps = job_steps(load_workflow("renovate-config.yml"), "renovate-config")
        command = str(next(step["run"] for step in steps if step.get("id") == "inputs"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "outputs"

            def git(*args: str) -> str:
                result = subprocess.run(  # noqa: S603
                    ["git", *args],  # noqa: S607
                    cwd=root,
                    check=True,
                    capture_output=True,
                    text=True,
                    env={
                        **os.environ,
                        "GIT_AUTHOR_NAME": "Fixture",
                        "GIT_AUTHOR_EMAIL": "fixture@example.invalid",
                        "GIT_COMMITTER_NAME": "Fixture",
                        "GIT_COMMITTER_EMAIL": "fixture@example.invalid",
                    },
                )
                return result.stdout.strip()

            _ = git("init", "--quiet")
            previous = ""
            manifests = (
                "renovate.json",
                ".github/scripts/check_renovate.py",
                ".github/scripts/test_check_renovate.py",
                ".github/workflows/renovate-config.yml",
                "Directory.Packages.props",
                "global.json",
                ".config/dotnet-tools.json",
                "src/new/new.csproj",
                ".github/scripts/requirements.txt",
                "Dockerfile",
                "compose.debug.yaml",
                "new-manager/package.json",
            )
            for name in manifests:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                _ = path.write_text("fixture\n", encoding="utf-8")
                _ = git("add", "--", name)
            for index, operation in enumerate(
                ("initial", *manifests, "add", "rename", "delete")
            ):
                if operation in manifests:
                    _ = (root / operation).write_text(
                        "changed input or image digest\n", encoding="utf-8"
                    )
                elif operation == "add":
                    _ = (root / "requirements-new.txt").write_text(
                        "ruff==0.1.0\n", encoding="utf-8"
                    )
                elif operation == "rename":
                    (root / "requirements-new.txt").rename(
                        root / "requirements-other.txt"
                    )
                elif operation == "delete":
                    (root / "requirements-other.txt").unlink()
                _ = git("add", "--all", "--", ".", ":(exclude)outputs")
                _ = git(
                    "-c",
                    "core.hooksPath=",
                    "commit",
                    "--quiet",
                    "--no-gpg-sign",
                    "-m",
                    f"fixture {index}",
                )
                output.unlink(missing_ok=True)
                result = run_bash(command, root, {"GITHUB_OUTPUT": output.as_posix()})
                self.assertEqual(result.returncode, 0, result.stderr)
                values = dict(
                    line.split("=", 1)
                    for line in output.read_text(encoding="utf-8").splitlines()
                )
                self.assertEqual(values["tree"], git("rev-parse", "HEAD^{tree}"))
                self.assertNotEqual(values["tree"], previous)
                previous = values["tree"]
                self.assertRegex(values["day"], r"^\d{4}-\d{2}-\d{2}$")


class NugetAuditWorkflowTests(unittest.TestCase):
    def test_report_requires_successful_forced_locked_restore(self):
        steps = job_steps(load_workflow("dependency-audit.yml"), "nuget-audit")
        restore = next(
            step for step in steps if "dotnet restore" in str(step.get("run", ""))
        )
        report = next(
            step for step in steps if "dotnet package list" in str(step.get("run", ""))
        )
        self.assertLess(steps.index(restore), steps.index(report))
        self.assertEqual(restore["run"], "dotnet restore --locked-mode --force")
        for step in (restore, report):
            self.assertNotIn("continue-on-error", step)
            self.assertNotIn("if", step)
        command = str(report["run"])
        for flag in (
            "--no-restore",
            "--vulnerable",
            "--include-transitive",
            "--format json",
        ):
            self.assertIn(flag, command)
        self.assertIn(
            'python .github/scripts/check_nuget_audit.py --report "$RUNNER_TEMP/nuget-audit.json" --root .',
            command,
        )


class PythonAnalysisTests(unittest.TestCase):
    def test_real_type_error_remains_visible_and_fails_workflow(self):
        steps = job_steps(load_workflow("ci.yml"), "contracts")
        command = str(
            next(
                step["run"]
                for step in steps
                if "basedpyright --project" in str(step.get("run", ""))
            )
        )
        checker = shutil.which("basedpyright")
        self.assertIsNotNone(
            checker, "Тест требует установленный pinned basedpyright из requirements"
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / ".github/scripts"
            scripts.mkdir(parents=True)
            _ = (scripts / "bad_type.py").write_text(
                'value: int = "wrong"\n', encoding="utf-8"
            )
            _ = (root / ".github/pyrightconfig.json").write_text(
                '{"include": ["scripts"]}', encoding="utf-8"
            )
            executable = shlex.quote(Path(cast(str, checker)).as_posix())
            result = run_bash(
                f'basedpyright() {{ {executable} "$@"; }}\n' + command,
                root,
                {"RUNNER_TEMP": root.as_posix()},
            )
            self.assertEqual(result.returncode, 1, result.stderr)
            report = cast(dict[str, object], json.loads(result.stdout))
            summary = cast(dict[str, int], report["summary"])
            self.assertEqual(summary["errorCount"], 1)
            self.assertEqual(summary["filesAnalyzed"], 1)
            self.assertIn("reportAssignmentType", result.stdout)
            self.assertEqual(
                json.loads(
                    (root / "basedpyright-report.json").read_text(encoding="utf-8")
                ),
                report,
            )

    def test_pip_cache_and_single_analysis_preserve_diagnostics_and_exit_code(self):
        steps = job_steps(load_workflow("ci.yml"), "contracts")
        setup = next(
            step
            for step in steps
            if "actions/setup-python@" in str(step.get("uses", ""))
        )
        settings = cast(dict[str, str], setup["with"])
        self.assertEqual(settings["cache"], "pip")
        self.assertEqual(
            settings["cache-dependency-path"], ".github/scripts/requirements.txt"
        )
        install = next(
            step for step in steps if "pip install" in str(step.get("run", ""))
        )
        self.assertNotIn("if", install)
        analysis = next(
            step
            for step in steps
            if "basedpyright --project" in str(step.get("run", ""))
        )
        command = str(analysis["run"])
        self.assertEqual(analysis["shell"], "bash")
        self.assertEqual(command.count("basedpyright --project"), 1)
        count = len(list((ROOT / ".github/scripts").rglob("*.py")))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checker = root / "basedpyright"
            report = {
                "generalDiagnostics": [
                    {"severity": "warning", "message": "диагностика фикстуры"}
                ],
                "summary": {"filesAnalyzed": count},
            }
            _ = checker.write_text(
                "#!/usr/bin/env python\nimport json, os, sys\n"
                f"print(json.dumps({report!r}))\n"
                "sys.exit(int(os.environ['CHECKER_EXIT']))\n",
                encoding="utf-8",
            )
            checker.chmod(0o755)
            for code in (0, 1, 2, 3, 4, 7):
                with self.subTest(code=code):
                    result = run_bash(
                        stub_command("basedpyright", checker) + command,
                        ROOT,
                        {"RUNNER_TEMP": root.as_posix(), "CHECKER_EXIT": str(code)},
                    )
                    self.assertEqual(result.returncode, code, result.stderr)
                    self.assertEqual(json.loads(result.stdout), report)
                    saved = root / "basedpyright-report.json"
                    self.assertEqual(
                        json.loads(saved.read_text(encoding="utf-8")), report
                    )
