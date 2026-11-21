"""Поведенческие контракты маршрутизации CI и доверенной публикации покрытия.

Проверяются фактические события, разрешения, порядок шагов, пины и
доверительные условия, а не «процент покрытия YAML»: сценарная матрица
перечислена явно, а hosted-сценарии остаются непроверяемыми и не выдаются за
успешные.
"""

from __future__ import annotations

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path
from typing import cast, override
from xml.etree import ElementTree

import test_check_dotnet_coverage as dotnet_fixture
import yaml
from check_dotnet_coverage import ClassEntry, _line_errors  # pyright: ignore[reportPrivateUsage]
from check_python_coverage_report import (
    Block as CoverageBlock,
)
from check_python_coverage_report import (
    check_source_errors,
    coverage_version_errors,
)
from check_python_coverage_report import (
    coverage_pin as validator_coverage_pin,
)

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github/workflows"
SCRIPTS = ROOT / ".github/scripts"
FIXTURES = SCRIPTS / "fixtures/coverage_workflow"
CODECOV_CONFIG = ROOT / "codecov.yml"
DOTNET_SETTINGS = ROOT / ".github/coverage/dotnet/dotnet-coverage.settings.xml"
PYTHON_VALIDATOR = SCRIPTS / "check_python_coverage_report.py"
NORMALIZER = SCRIPTS / "normalize_lcov_paths.py"
COBERTURA_NORMALIZER = SCRIPTS / "normalize_cobertura_paths.py"
DOTNET_VALIDATOR = SCRIPTS / "check_dotnet_coverage.py"
CODECOV_ACTION = "codecov/codecov-action@303a32d7a59b442fa8d48b6a1cc6825c09c847a5"
UPLOAD_ARTIFACT = "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9"
DOWNLOAD_ARTIFACT = "actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333"
EXPECTED_FLAGS = ("dotnet-unit", "dotnet-docker", "python-linux", "python-windows")
CI_JOBS = frozenset(
    ("actions-security", "contracts", "background-wait-windows", "build")
)
ENV_SECRET = "${{ secrets.CODECOV_ENV_TOKEN }}"  # noqa: S105 - выражение контекста, не секрет
UPLOAD_IDS = (
    "upload-dotnet-unit",
    "upload-dotnet-docker",
    "upload-python-linux",
    "upload-python-windows",
)
PUBLISH_GATE = (
    "github.event.workflow_run.conclusion == 'success' && "
    "(github.event.workflow_run.event == 'push' || "
    "(github.event.workflow_run.event == 'pull_request' && "
    "github.event.workflow_run.head_repository.full_name == github.repository))"
)


def load_workflow(name: str) -> dict[str, object]:
    """Прочитать workflow как строковые скаляры: `on` не должен стать bool."""
    return cast(
        dict[str, object],
        yaml.load(
            (WORKFLOWS / name).read_text(encoding="utf-8"),
            Loader=yaml.BaseLoader,  # noqa: S506 - BaseLoader не создаёт объекты
        ),
    )


def workflow_text(name: str) -> str:
    return (WORKFLOWS / name).read_text(encoding="utf-8")


def workflow_artifact_retention_days() -> int:
    """Срок хранения артефактов покрытия, объявленный в CI."""
    text = workflow_text("ci.yml")
    declared = {
        int(match.group(1)) for match in re.finditer(r"retention-days:\s*(\d+)", text)
    }
    if len(declared) != 1:
        raise AssertionError(
            f"ожидался один retention-days, найдено {sorted(declared)}"
        )
    return declared.pop()


def job_steps(workflow: dict[str, object], job: str) -> list[dict[str, object]]:
    jobs = cast(dict[str, dict[str, object]], workflow["jobs"])
    return cast(list[dict[str, object]], jobs[job]["steps"])


def ci_steps(job: str) -> list[dict[str, object]]:
    return job_steps(load_workflow("ci.yml"), job)


def publisher_steps(job: str) -> list[dict[str, object]]:
    return job_steps(load_workflow("coverage-publisher.yml"), job)


def find_step(steps: list[dict[str, object]], needle: str) -> dict[str, object]:
    for step in steps:
        if needle in str(step.get("run", "")) or needle in str(step.get("name", "")):
            return step
    raise AssertionError(f"не найден шаг с признаком {needle!r}")


def index_of(steps: list[dict[str, object]], needle: str) -> int:
    for index, step in enumerate(steps):
        if needle in str(step.get("run", "")) or needle in str(step.get("name", "")):
            return index
    raise AssertionError(f"не найден шаг с признаком {needle!r}")


def bash_command() -> str:
    """Найти Bash для реального исполнения фрагментов условий."""
    if os.name == "nt":
        git = shutil.which("git")
        if git:
            bundled = Path(git).resolve().parents[1] / "bin/bash.exe"
            if bundled.is_file():
                return str(bundled)
    bash = shutil.which("bash")
    if not bash:
        raise RuntimeError("Для исполнения условий workflow требуется Bash")
    return bash


def run_bash(script: str, env: dict[str, str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(  # noqa: S603 - фиксированная команда без внешних данных
        [bash_command(), "--noprofile", "--norc", "-eo", "pipefail", "-c", script],
        cwd=ROOT,
        env={**os.environ, **env},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def to_bash(expression: str) -> str:
    """Перевести условие шага в bash-выражение с подставленными env."""
    result = " ".join(expression.split())
    for source, target in (
        ("github.event.workflow_run.head_repository.full_name", '"${HEAD_REPO}"'),
        ("github.event.workflow_run.event", '"${RUN_EVENT}"'),
        ("github.event.workflow_run.conclusion", '"${RUN_CONCLUSION}"'),
        ("github.event.pull_request.head.repo.full_name", '"${HEAD_REPO}"'),
        ("github.event_name", '"${EVENT_NAME}"'),
        ("github.repository", '"${REPOSITORY}"'),
    ):
        result = result.replace(source, target)
    result = re.sub(r"'([^']*)'", r'"\1"', result)
    result = result.replace("!(", "! (")
    return f"[[ {result} ]]"


def evaluate(
    condition: str, *, event: str, conclusion: str = "success", head_repo: str = ""
) -> str:
    """Выполнить реальное условие шага и вернуть allowed/denied."""
    script = f"if {to_bash(condition)}; then echo allowed; else echo denied; fi"
    result = run_bash(
        script,
        {
            "RUN_EVENT": event,
            "RUN_CONCLUSION": conclusion,
            "EVENT_NAME": event,
            "HEAD_REPO": head_repo,
            "REPOSITORY": "Flandre-Scarlet-the-best/anketa-parser",
        },
    )
    if result.returncode != 0:
        raise AssertionError(result.stderr)
    return result.stdout.strip()


def run_script(script: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    """Запустить отслеживаемый скрипт без shell: пути и аргументы фиксированы."""
    return subprocess.run(  # noqa: S603
        [sys.executable, str(script), *arguments],
        cwd=ROOT,
        env={**os.environ, "PYTHONIOENCODING": "utf-8"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def triggers(name: str) -> set[str]:
    workflow = load_workflow(name)
    events = workflow["on"]
    if isinstance(events, str):
        return {events}
    return set(cast("dict[str, object] | list[str]", events))


class RoutingContractTests(unittest.TestCase):
    """ci.yml исполняет код PR и merge_group, поэтому секретов в нём быть не может."""

    workflow: dict[str, object]
    text: str

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.workflow = {}
        self.text = ""

    @override
    def setUp(self) -> None:
        self.workflow = load_workflow("ci.yml")
        self.text = workflow_text("ci.yml")

    def test_events_jobs_and_read_only_permissions_are_preserved(self):
        self.assert_ci_routing(self.workflow)

    def assert_ci_routing(self, workflow: dict[str, object]) -> None:
        all_triggers = cast(dict[str, object], workflow["on"])
        self.assertEqual(set(all_triggers), {"push", "pull_request", "merge_group"})
        self.assertEqual(
            cast(dict[str, list[str]], all_triggers["push"])["branches"], ["main"]
        )
        self.assertEqual(
            cast(dict[str, str], workflow["permissions"]), {"contents": "read"}
        )
        jobs = cast(dict[str, dict[str, object]], workflow["jobs"])
        self.assertLessEqual(CI_JOBS, jobs.keys())
        for name, raw in jobs.items():
            permissions = raw.get("permissions", workflow["permissions"])
            if isinstance(permissions, str):
                self.assertEqual(permissions, "read-all", name)
                continue
            grants = cast(dict[str, str], permissions)
            self.assertTrue(
                all(value in {"read", "none"} for value in grants.values()), name
            )

    def test_independent_read_only_job_is_allowed(self):
        workflow = deepcopy(self.workflow)
        jobs = cast(dict[str, object], workflow["jobs"])
        jobs["independent"] = {"permissions": {"actions": "read"}, "steps": []}
        self.assert_ci_routing(workflow)

    def test_independent_job_cannot_acquire_write_permissions(self):
        for permissions in ("write-all", {"contents": "write"}, {"id-token": "write"}):
            with self.subTest(permissions=permissions):
                workflow = deepcopy(self.workflow)
                jobs = cast(dict[str, object], workflow["jobs"])
                jobs["independent"] = {"permissions": permissions}
                with self.assertRaisesRegex(AssertionError, "independent"):
                    self.assert_ci_routing(workflow)

    def test_ci_workflow_contains_no_repository_secret(self):
        self.assertNotIn("secrets.", self.text)

    def test_no_step_interpolates_expressions_into_shell(self):
        for job in cast(dict[str, dict[str, object]], self.workflow["jobs"]).values():
            for step in cast(list[dict[str, object]], job.get("steps", [])):
                self.assertNotIn("${{", str(step.get("run", "")), str(step.get("name")))

    def test_no_step_weakens_failure_handling(self):
        self.assertNotIn("continue-on-error", self.text)
        self.assertNotIn("|| true", self.text)
        for job in cast(dict[str, dict[str, object]], self.workflow["jobs"]).values():
            for step in cast(list[dict[str, object]], job.get("steps", [])):
                self.assertNotIn("|| true", str(step.get("run", "")))


class SecretBoundaryTests(unittest.TestCase):
    """Секреты разрешены только доверенному издателю покрытия."""

    def assert_secret_boundary(self, name: str, events: set[str], text: str) -> None:
        expressions = re.finditer(r"\$\{\{(.*?)\}\}", text, re.DOTALL)
        uses_context = any(
            re.search(
                r"(?<![.\w])secrets\b",
                re.sub(r"'(?:[^']|'')*'", "", expression.group(1)),
                re.IGNORECASE,
            )
            for expression in expressions
        )
        passes_secrets = re.search(r"(?m)(?:^|[{,])\s*secrets\s*:", text)
        if uses_context or passes_secrets:
            self.assertEqual(events, {"workflow_run"}, f"{name}: недоверенное событие")
            self.assertEqual(
                name, "coverage-publisher.yml", "неразрешённый получатель секрета"
            )

    def test_only_the_trusted_publisher_uses_repository_secrets(self):
        for path in sorted(WORKFLOWS.iterdir()):
            if path.suffix in {".yml", ".yaml"}:
                self.assert_secret_boundary(
                    path.name, triggers(path.name), path.read_text(encoding="utf-8")
                )

    def test_secrets_are_rejected_on_untrusted_triggers(self):
        for event in ("pull_request", "merge_group"):
            for secret in (
                ENV_SECRET,
                "${{ secrets['EXAMPLE'] }}",
                "${{ toJSON(secrets) }}",
                "  secrets: inherit",
            ):
                with (
                    self.subTest(event=event, secret=secret),
                    self.assertRaisesRegex(AssertionError, "недоверенное событие"),
                ):
                    self.assert_secret_boundary("ci.yml", {event}, secret)

    def test_only_publisher_may_receive_secrets_even_on_trusted_trigger(self):
        with self.assertRaisesRegex(AssertionError, "неразрешённый получатель"):
            self.assert_secret_boundary(
                "independent.yaml", {"workflow_run"}, ENV_SECRET
            )

    def test_self_token_is_not_a_repository_secret(self):
        for path in sorted(WORKFLOWS.glob("*.yml")):
            for line in path.read_text(encoding="utf-8").splitlines():
                if "github.token" in line:
                    self.assertNotIn("secrets.", line, path.name)


class ProducerJobTests(unittest.TestCase):
    def test_dotnet_reports_are_measured_normalized_validated_and_stored(self):
        steps = ci_steps("build")
        order = [
            "dotnet restore --locked-mode",
            "dotnet build --no-restore -c Release",
            "Измерить покрытие unit-тестов",
            "Нормализовать пути отчёта unit-тестов",
            "Проверить отчёт покрытия unit-тестов",
            "Сохранить отчёт покрытия unit-тестов",
            "Измерить покрытие изолированной PostgreSQL",
            "Нормализовать пути отчёта Docker-тестов",
            "Проверить отчёт покрытия Docker-тестов",
            "Сохранить отчёт покрытия Docker-тестов",
        ]
        positions = [index_of(steps, needle) for needle in order]
        self.assertEqual(positions, sorted(positions))
        for flavour, label in (("unit", "unit-тестов"), ("docker", "Docker-тестов")):
            normalizer = find_step(steps, f"Нормализовать пути отчёта {label}")
            self.assertIn(
                f"python .github/scripts/normalize_cobertura_paths.py --report TestResults/dotnet-{flavour}/dotnet.cobertura.xml --repo-root .",
                str(normalizer["run"]),
            )
            validator = find_step(steps, f"Проверить отчёт покрытия {label}")
            self.assertIn(
                f"python .github/scripts/check_dotnet_coverage.py TestResults/dotnet-{flavour} {flavour}",
                str(validator["run"]),
            )
            self.assertIn(
                f"--instrumented-report TestResults/dotnet-{flavour}/instrumented.xml",
                str(validator["run"]),
            )
        for label in (
            "Измерить покрытие unit-тестов",
            "Измерить покрытие изолированной PostgreSQL",
        ):
            measure = str(find_step(steps, label)["run"])
            for flag in (
                "--coverage",
                f"--coverage-settings {DOTNET_SETTINGS.relative_to(ROOT).as_posix()}",
                "--no-build",
            ):
                self.assertIn(flag, measure)
            self.assertNotIn("--coverage-output", measure)
        settings = ElementTree.fromstring(DOTNET_SETTINGS.read_text(encoding="utf-8"))  # noqa: S314
        self.assertEqual(settings.findtext("Format"), "cobertura,xml")
        artifacts = [
            step
            for step in steps
            if "actions/upload-artifact" in str(step.get("uses", ""))
        ]
        self.assertEqual(len(artifacts), 2)
        for flavour, step in zip(("unit", "docker"), artifacts, strict=True):
            self.assertTrue(str(step["uses"]).startswith(UPLOAD_ARTIFACT))
            artifact = cast(dict[str, str], step["with"])
            self.assertEqual(artifact["if-no-files-found"], "error")
            self.assertEqual(artifact["name"], f"dotnet-coverage-{flavour}")
            self.assertIn(
                f"TestResults/dotnet-{flavour}/instrumented.xml",
                artifact["path"].splitlines(),
            )

    def test_python_pipeline_is_ordered_on_both_platforms(self):
        for job, discover, platform, shell in (
            ("contracts", "test_*.py", "linux", "posix"),
            ("background-wait-windows", "test_background_wait.py", "windows", "pwsh"),
        ):
            with self.subTest(job=job):
                steps = ci_steps(job)
                chain = [
                    "coverage erase",
                    "coverage run -m unittest discover",
                    "coverage combine",
                    "coverage report --show-missing",
                    "coverage lcov",
                    "normalize_lcov_paths.py",
                    f"--platform {platform}",
                ]
                positions = [index_of(steps, needle) for needle in chain]
                self.assertEqual(positions, sorted(positions), chain)
                self.assertIn(
                    discover,
                    str(find_step(steps, "coverage run -m unittest discover")["run"]),
                )
                validator = find_step(steps, f"--platform {platform}")
                self.assertIn("--data-dir", str(validator["run"]))
                artifact = [
                    step
                    for step in steps
                    if "actions/upload-artifact" in str(step.get("uses", ""))
                ]
                self.assertEqual(len(artifact), 1)
                self.assertEqual(
                    cast(dict[str, str], artifact[0]["with"])["name"],
                    f"python-coverage-{platform}",
                )
                if shell == "pwsh":
                    run = " ".join(str(step.get("run", "")) for step in steps)
                    self.assertNotIn("sed ", run)
                    self.assertIn("$env:COVERAGE_DIR", run)

    def test_windows_job_pins_the_same_coverage_version_without_posix_tools(self):
        pin = coverage_pin()
        steps = ci_steps("background-wait-windows")
        install = find_step(steps, "pip install")
        self.assertIn(f"coverage{pin}", str(install["run"]))
        self.assertNotIn("sed", str(install["run"]))

    @unittest.skipUnless(
        os.name == "nt", "Проверка шага PowerShell выполняется в Windows"
    )
    def test_windows_coverage_stops_at_failed_test_command(self):
        script = str(
            find_step(
                ci_steps("background-wait-windows"), "coverage run -m unittest discover"
            )["run"]
        )
        runner = """\
$ErrorActionPreference = 'Stop'
function python {
    Write-Output $args[2]
    $global:LASTEXITCODE = 0
    if ($args[2] -eq 'run') { $global:LASTEXITCODE = 23 }
}
"""
        result = subprocess.run(  # noqa: S603
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", runner + script],  # noqa: S607
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ["erase", "run"])

    def test_only_head_or_push_artifacts_contain_the_checked_out_revision(self):
        upload_if = "${{ github.event_name == 'push' || matrix.revision == 'head' }}"
        for job in ("contracts", "background-wait-windows", "build"):
            with self.subTest(job=job):
                steps = ci_steps(job)
                checkout = next(
                    step
                    for step in steps
                    if "actions/checkout@" in str(step.get("uses", ""))
                )
                settings = cast(dict[str, str], checkout["with"])
                self.assertEqual(
                    settings["ref"],
                    "${{ matrix.revision == 'head' && github.event.pull_request.head.sha || '' }}",
                )
                for index, step in enumerate(steps):
                    if "actions/upload-artifact@" not in str(step.get("uses", "")):
                        continue
                    record = steps[index - 1]
                    self.assertEqual(record["if"], upload_if)
                    self.assertIn("git rev-parse HEAD", str(record["run"]))
                    self.assertEqual(step["if"], upload_if)
                    self.assertIn(
                        "measured-commit.txt",
                        cast(dict[str, str], step["with"])["path"],
                    )


def coverage_pin() -> str:
    requirements = (SCRIPTS / "requirements.txt").read_text(encoding="utf-8")
    match = re.search(r"(?m)^coverage(==\S+)$", requirements)
    if match is None:
        raise AssertionError("в requirements.txt нет точного пина coverage")
    return match.group(1)


class PublisherWorkflowTests(unittest.TestCase):
    """Доверенный издатель: workflow_run, environment secret, без кода PR."""

    workflow: dict[str, object]
    steps: list[dict[str, object]]
    text: str

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.workflow = {}
        self.steps = []
        self.text = ""

    @override
    def setUp(self) -> None:
        self.workflow = load_workflow("coverage-publisher.yml")
        self.steps = job_steps(self.workflow, "publish")
        self.text = workflow_text("coverage-publisher.yml")

    def test_trigger_is_trusted_workflow_run_only(self):
        self.assertEqual(triggers("coverage-publisher.yml"), {"workflow_run"})
        run = cast(
            dict[str, object],
            cast(dict[str, object], self.workflow["on"])["workflow_run"],
        )
        self.assertEqual(run["workflows"], ["CI"])
        self.assertEqual(run["types"], ["completed"])

    def test_publish_job_is_gated_and_declares_environment(self):
        job = cast(dict[str, dict[str, object]], self.workflow["jobs"])["publish"]
        self.assertEqual(" ".join(str(job["if"]).split()), PUBLISH_GATE)
        self.assertEqual(cast(dict[str, str], job["environment"]), {"name": "codecov"})
        self.assertLessEqual(
            set(cast(dict[str, str], job["permissions"])),
            {"contents", "actions"},
        )
        self.assertNotIn("id-token", self.text)
        self.assertNotIn("pull_request_target", self.text)

    def test_environment_secret_has_distinct_name_and_is_guarded(self):
        uploads = [
            step
            for step in self.steps
            if "codecov/codecov-action" in str(step.get("uses", ""))
        ]
        self.assertEqual(len(uploads), 4)
        for step in uploads:
            settings = cast(dict[str, str], step["with"])
            self.assertEqual(settings["token"], ENV_SECRET)
        # Отдельное имя: отсутствие настройки не даёт незаметного fallback на
        # прежний repository secret.
        self.assertNotIn("secrets.CODECOV_TOKEN", self.text)
        guard = find_step(self.steps, "Проверить наличие секрета окружения")
        self.assertEqual(cast(dict[str, str], guard["env"]), {"ENV_TOKEN": ENV_SECRET})
        body = str(guard["run"])
        self.assertIn("::error::", body)
        self.assertIn("Публикация не выполняется", body)
        self.assertNotIn('echo "${ENV_TOKEN}"', body)
        # Задание не выдаёт объявление environment за доказательство защиты.
        self.assertIn("НЕ является доказательством защиты", self.text)

    def test_manual_checklist_is_documented_in_the_workflow(self):
        for required in (
            "Создать environment `codecov`",
            "environment secret `CODECOV_ENV_TOKEN`",
            "Required reviewers",
            "УДАЛИТЬ repository-level secret `CODECOV_TOKEN`",
            "Проверить руками",
        ):
            self.assertIn(required, self.text)

    def test_source_tree_is_fetched_only_as_data(self):
        checkouts = [
            step
            for step in self.steps
            if "actions/checkout@" in str(step.get("uses", ""))
        ]
        self.assertEqual(len(checkouts), 2)
        trusted, source = checkouts
        self.assertNotIn("ref", cast(dict[str, str], trusted.get("with", {})))
        settings = cast(dict[str, str], source["with"])
        self.assertEqual(settings["ref"], "${{ github.event.workflow_run.head_sha }}")
        self.assertEqual(settings["path"], "source-tree")
        self.assertEqual(settings["persist-credentials"], "false")
        self.assertEqual(settings["lfs"], "false")
        self.assertEqual(settings["submodules"], "false")
        # Валидаторы запускаются из default branch, исходники читаются как данные.
        for needle in (
            "check_dotnet_coverage.py coverage/dotnet-unit unit --repo-root source-tree",
            "check_dotnet_coverage.py coverage/dotnet-docker docker --repo-root source-tree",
            "check_python_coverage_report.py --report coverage/python-linux/lcov.info",
            "check_python_coverage_report.py --report coverage/python-windows/lcov.info",
        ):
            step = find_step(self.steps, needle)
            self.assertIn("--repo-root source-tree", str(step["run"]))
        for flavour in ("unit", "docker"):
            step = find_step(
                self.steps, f"check_dotnet_coverage.py coverage/dotnet-{flavour}"
            )
            argv = shlex.split(str(step["run"]))
            self.assertNotIn("--max-age-hours", argv)
            self.assertIn(
                f"--instrumented-report coverage/dotnet-{flavour}/instrumented.xml",
                str(step["run"]),
            )

    def test_source_tree_integrity_guard_rejects_execution_vectors(self):
        script = str(find_step(self.steps, "Проверить целостность source-tree")["run"])
        cases: tuple[tuple[str, dict[str, str], int], ...] = (
            ("clean", {"workflow.yml": "literal", "header.md": "header"}, 0),
            ("submodule", {".gitmodules": "file"}, 1),
            ("lfs", {"payload.bin": "lfs"}, 1),
            ("hook", {".git/hooks/pre-commit": "hook"}, 1),
            ("symlink-outside", {"outside-link": "symlink"}, 1),
        )
        for name, layout, expected in cases:
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                tree = root / "source-tree"
                tree.mkdir(parents=True, exist_ok=True)
                for relative, kind in layout.items():
                    target = tree / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if kind == "symlink":
                        outside = root / "outside.txt"
                        _ = outside.write_text("x", encoding="utf-8")
                        try:
                            target.symlink_to(outside)
                        except OSError as error:
                            # Windows без права создавать симлинки: случай
                            # проверяется на Linux-исполнителе CI.
                            raise unittest.SkipTest(
                                f"симлинки недоступны: {error}"
                            ) from error
                    elif kind == "lfs":
                        _ = target.write_text(
                            "version https://git-lfs.github.com/spec/v1\n"
                            + f"oid sha256:{'a' * 64}\nsize 5\n",
                            encoding="utf-8",
                            newline="\n",
                        )
                    elif kind in ("literal", "header"):
                        _ = target.write_text(
                            'example: "version https://git-lfs.github.com/spec/v1"\n'
                            if kind == "literal"
                            else "version https://git-lfs.github.com/spec/v1\nПример из документации\n",
                            encoding="utf-8",
                            newline="\n",
                        )
                    else:
                        _ = target.write_text("x", encoding="utf-8")
                result = run_bash_in(script, root, {"SOURCE_TREE": "source-tree"})
                self.assertEqual(
                    result.returncode,
                    expected,
                    result.stdout + result.stderr,
                )
                if expected:
                    self.assertIn("::error::", result.stdout + result.stderr)

    def test_delayed_publisher_still_requires_matching_measured_revision(self):
        script = str(find_step(self.steps, "Сверить измеренную ревизию")["run"])
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source-tree"
            source.mkdir()
            relative = "src/Example/Source.cs"
            dotnet_fixture.write_project(
                source / "src/Example/Example.csproj", "Example"
            )
            _ = (source / relative).write_text("class Example { }\n", encoding="utf-8")
            git = ["git", "-C", str(source)]
            for arguments in (
                ["init", "-q"],
                ["add", "src"],
                [
                    "-c",
                    "user.name=Test",
                    "-c",
                    "user.email=test@example.invalid",
                    "commit",
                    "-qm",
                    "Проверка",
                ],
            ):
                _ = subprocess.run([*git, *arguments], check=True, capture_output=True)  # noqa: S603
            revision = subprocess.check_output(  # noqa: S603
                [*git, "rev-parse", "HEAD"], text=True
            ).strip()
            for flag in EXPECTED_FLAGS:
                manifest = root / "coverage" / flag / "measured-commit.txt"
                manifest.parent.mkdir(parents=True)
                _ = manifest.write_text(revision, encoding="ascii")
            env = {"EXPECTED_COMMIT": revision}
            success = run_bash_in(script, root, env)
            self.assertEqual(success.returncode, 0, success.stdout + success.stderr)
            measured = root / "coverage/dotnet-unit"
            _ = (measured / "dotnet.cobertura.xml").write_text(
                dotnet_fixture.make_report(
                    [("Example", relative, {1: 1})], timestamp=1_700_000_000
                ),
                encoding="utf-8",
            )
            _ = (measured / "instrumented.xml").write_text(
                dotnet_fixture.make_instrumented_report(
                    {"Example": [(relative, True)]}
                ),
                encoding="utf-8",
            )
            validator = find_step(
                self.steps, "check_dotnet_coverage.py coverage/dotnet-unit"
            )
            _, path, *arguments = shlex.split(str(validator["run"]))
            checked = subprocess.run(  # noqa: S603 - доверенный валидатор и локальная фикстура
                [sys.executable, str(ROOT / path), *arguments],
                cwd=root,
                capture_output=True,
                text=True,
                encoding="utf-8",
                check=False,
            )
            self.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)
            wrong_source = run_bash_in(script, root, {"EXPECTED_COMMIT": "a" * 40})
            self.assertNotEqual(wrong_source.returncode, 0)
            self.assertIn("Исходники не соответствуют", wrong_source.stdout)
            windows = root / "coverage/python-windows/measured-commit.txt"
            _ = windows.write_text("a" * 40, encoding="ascii")
            for missing in (False, True):
                with self.subTest(missing=missing):
                    if missing:
                        windows.unlink()
                    result = run_bash_in(script, root, env)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("Отчёт coverage/python-windows", result.stdout)

    def test_identity_step_rejects_ambiguous_or_foreign_identity(self):
        script = str(find_step(self.steps, "Проверить идентичность")["run"])
        sha = "a" * 40
        other = "b" * 40
        repo = "Flandre-Scarlet-the-best/anketa-parser"
        one_pr = json.dumps([{"number": 7, "head": {"sha": sha}}])
        two_prs = json.dumps([{"number": 7, "head": {"sha": sha}}, {"number": 8}])
        cases = (
            ("push", "[]", "", sha, repo, 0),
            ("pull_request", one_pr, "7", sha, repo, 0),
            ("pull_request", "[]", "", sha, repo, 1),
            ("pull_request", two_prs, "7", sha, repo, 1),
            ("pull_request", one_pr, "7", other, repo, 1),
            ("pull_request", one_pr, "7", sha, "external/fork", 1),
            ("push", "[]", "", "not-a-sha", repo, 1),
            ("merge_group", "[]", "", sha, repo, 1),
        )
        for event, pr_count, pr_number, head_sha, head_repo, expected in cases:
            with (
                self.subTest(event=event, count=pr_count, sha=head_sha),
                tempfile.TemporaryDirectory() as temporary,
            ):
                output = Path(temporary) / "output"
                _ = output.write_text("", encoding="utf-8")
                result = run_bash_in(
                    script,
                    Path(temporary),
                    {
                        "GITHUB_OUTPUT": output.as_posix(),
                        "RUN_EVENT": event,
                        "HEAD_SHA": head_sha,
                        "HEAD_REPO": head_repo,
                        "REPOSITORY": repo,
                        "PR_COUNT": pr_count,
                        "PR_NUMBER": pr_number,
                        "PR_SHA": sha if one_pr in pr_count else "",
                    },
                )
                self.assertEqual(
                    result.returncode, expected, result.stdout + result.stderr
                )
                written = output.read_text(encoding="utf-8")
                if expected == 0:
                    self.assertIn("commit=" + head_sha, written)
                    if event == "push":
                        self.assertIn("pr=\n", written + "\n")
                    else:
                        self.assertIn("pr=7", written)

    def test_uploads_use_verified_identity_and_pinned_action(self):
        uploads = [
            step
            for step in self.steps
            if "codecov/codecov-action" in str(step.get("uses", ""))
        ]
        mapping = {
            cast(dict[str, str], step["with"])["flags"]: cast(
                dict[str, str], step["with"]
            )
            for step in uploads
        }
        self.assertEqual(tuple(sorted(mapping)), tuple(sorted(EXPECTED_FLAGS)))
        for step in uploads:
            self.assertEqual(str(step["uses"]), CODECOV_ACTION)
            settings = cast(dict[str, str], step["with"])
            self.assertEqual(settings["disable_search"], "true")
            # Список файлов network-секции строится из измеренного дерева, а не
            # из checkout ветки по умолчанию, где новых файлов PR ещё нет.
            self.assertEqual(settings["root_dir"], "source-tree")
            # Загружаются четыре явных файла: плагины загрузчика не нужны и
            # не должны исполнять код измеренной ревизии.
            self.assertEqual(settings["plugins"], "noop")
            self.assertEqual(settings["handle_no_reports_found"], "false")
            self.assertEqual(settings["fail_ci_if_error"], "true")
            self.assertEqual(settings["version"], "v11.3.2")
            self.assertEqual(settings["cleanup"], "true")
            self.assertEqual(
                settings["override_commit"], "${{ steps.identity.outputs.commit }}"
            )
            self.assertEqual(
                settings["override_pr"], "${{ steps.identity.outputs.pr }}"
            )
            self.assertNotIn("directory", settings)
        self.assertEqual([str(step.get("id")) for step in uploads], list(UPLOAD_IDS))

    def test_publish_gate_excludes_fork_merge_group_and_failed_runs(self):
        for event, conclusion, head_repo, expected in (
            ("push", "success", "Flandre-Scarlet-the-best/anketa-parser", "allowed"),
            ("push", "failure", "Flandre-Scarlet-the-best/anketa-parser", "denied"),
            (
                "pull_request",
                "success",
                "Flandre-Scarlet-the-best/anketa-parser",
                "allowed",
            ),
            ("pull_request", "success", "external/fork", "denied"),
            ("merge_group", "success", "", "denied"),
            ("merge_group", "success", "external/fork", "denied"),
        ):
            with self.subTest(event=event, conclusion=conclusion, head=head_repo):
                self.assertEqual(
                    evaluate(
                        PUBLISH_GATE,
                        event=event,
                        conclusion=conclusion,
                        head_repo=head_repo,
                    ),
                    expected,
                )

    def test_skipped_state_is_a_separate_explicit_job(self):
        jobs = cast(dict[str, dict[str, object]], self.workflow["jobs"])
        self.assertIn("not-published", jobs)
        skipped = jobs["not-published"]
        condition = " ".join(str(skipped["if"]).split())
        self.assertTrue(condition.startswith("!("), condition)
        for event, conclusion, head_repo, expected in (
            ("merge_group", "success", "", "allowed"),
            ("pull_request", "success", "external/fork", "allowed"),
            ("push", "success", "Flandre-Scarlet-the-best/anketa-parser", "denied"),
            (
                "pull_request",
                "success",
                "Flandre-Scarlet-the-best/anketa-parser",
                "denied",
            ),
        ):
            with self.subTest(event=event, head=head_repo):
                self.assertEqual(
                    evaluate(
                        condition,
                        event=event,
                        conclusion=conclusion,
                        head_repo=head_repo,
                    ),
                    expected,
                )
        steps = job_steps(self.workflow, "not-published")
        body = " ".join(str(step.get("run", "")) for step in steps)
        self.assertIn("не публиковалось", body)
        self.assertNotIn("опубликовано успешно", body)

    def test_summary_uses_real_step_outcomes(self):
        summary = find_step(self.steps, "Сводка публикации покрытия")
        self.assertEqual(summary.get("if"), "always()")
        env = cast(dict[str, str], summary["env"])
        self.assertEqual(
            set(env),
            {
                "OUTCOME_UNIT",
                "OUTCOME_DOCKER",
                "OUTCOME_LINUX",
                "OUTCOME_WINDOWS",
            },
        )
        for step_id in UPLOAD_IDS:
            self.assertIn(f"steps.{step_id}.outcome", " ".join(env.values()))
        run = str(summary["run"])
        self.assertIn("::warning::Частичная публикация", run)
        self.assertIn("::error::Ни один отчёт не загружен", run)
        self.assertIn("successes > 0", run)
        self.assertIn("successes == 4", run)
        self.assertNotIn("атомарн", workflow_text("coverage-publisher.yml"))

    def test_cancelled_run_is_described_honestly(self):
        step = find_step(self.steps, "Отметить отменённое выполнение")
        self.assertEqual(step.get("if"), "cancelled()")
        body = str(step["run"])
        self.assertIn("::warning::", body)
        self.assertIn("отменено", body)
        self.assertIn("неизвестно", body)


class PinAndSourceLimitTests(unittest.TestCase):
    """Единый пин coverage и пределы чтения исходников."""

    def test_every_workflow_pin_matches_requirements(self):
        pin = coverage_pin()
        literals: dict[str, list[str]] = {}
        for path in sorted(WORKFLOWS.glob("*.yml")):
            found = re.findall(r"coverage==([0-9][\w.]*)", path.read_text("utf-8"))
            if found:
                literals[path.name] = found
        self.assertTrue(literals, "не найден ни один пин coverage в workflow")
        expected = f"coverage{pin}"
        for name, values in literals.items():
            for value in values:
                self.assertEqual(f"coverage=={value}", expected, name)

    def test_publisher_does_not_install_the_whole_requirements(self):
        steps = job_steps(load_workflow("coverage-publisher.yml"), "publish")
        install = find_step(steps, "pip install")
        body = str(install["run"])
        self.assertIn(f"coverage{coverage_pin()}", body)
        self.assertNotIn("-r ", body)
        self.assertNotIn("requirements.txt", body)

    def test_python_validator_reports_version_mismatch(self):
        self.assertEqual(coverage_version_errors(), [])
        self.assertEqual(validator_coverage_pin(), f"coverage{coverage_pin()}")

    def test_python_validator_rejects_oversized_and_foreign_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            sandbox = Path(temporary)
            root = sandbox / "repo"
            source = root / ".github/scripts/big.py"
            source.parent.mkdir(parents=True)
            _ = source.write_text("value = 1\n", encoding="utf-8")
            block = CoverageBlock(
                lines={1: 1}, statements=1, hits=1, branches=1, branch_lines={1: 1}
            )
            oversized = check_source_errors(
                root, ".github/scripts/big.py", block, max_bytes=4
            )
            self.assertTrue(any("превышает предел" in error for error in oversized))
            outside = sandbox / "outside-module.py"
            _ = outside.write_text("value = 1\n", encoding="utf-8")
            escaped = check_source_errors(root, "../outside-module.py", block)
            self.assertTrue(
                any(
                    "вне корня" in error or "Нет исходника" in error
                    for error in escaped
                ),
                escaped,
            )

    def test_dotnet_validator_rejects_impossible_line_and_size(self):
        # Семантику точек следования C# без PDB не воспроизводим: измерено, что
        # реальный отчёт ссылается и на пустые строки внутри raw-литералов,
        # поэтому блокируется только достоверно невозможная строка.
        document = ElementTree.fromstring(  # noqa: S314 - локальная фикстура
            '<class filename="src/Lib/Source.cs"><lines><line number="99" hits="1"/></lines></class>'
        )
        entry = ClassEntry("src/Lib/Source.cs", document)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "src/Lib/Source.cs"
            source.parent.mkdir(parents=True)
            _ = source.write_text("class Lib\n\n{\n}\n", encoding="utf-8")
            errors = _line_errors([entry], root, "Проверка")
            self.assertTrue(
                any("строка вне исходника" in error for error in errors), errors
            )
            oversized = _line_errors([entry], root, "Проверка", max_source_bytes=4)
            self.assertTrue(
                any("превышает предел" in error for error in oversized), oversized
            )


def run_bash_in(
    script: str, cwd: Path, env: dict[str, str]
) -> subprocess.CompletedProcess[str]:
    """Выполнить фрагмент workflow в заданном каталоге (как на runner)."""
    return subprocess.run(  # noqa: S603 - команда из проверяемого workflow
        [bash_command(), "--noprofile", "--norc", "-uo", "pipefail", "-c", script],
        cwd=cwd,
        env={**os.environ, **env},
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


class CodecovConfigurationTests(unittest.TestCase):
    config: dict[str, object]

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.config = {}

    @override
    def setUp(self) -> None:
        self.config = cast(
            dict[str, object],
            yaml.load(
                CODECOV_CONFIG.read_text(encoding="utf-8"),
                Loader=yaml.BaseLoader,  # noqa: S506
            ),
        )

    def test_flags_match_workflow_uploads(self):
        flags = cast(dict[str, dict[str, str]], self.config["flags"])
        self.assertEqual(tuple(sorted(flags)), tuple(sorted(EXPECTED_FLAGS)))
        for name, settings in flags.items():
            self.assertEqual(settings["carryforward"], "false", name)
        notify = cast(
            dict[str, str], cast(dict[str, object], self.config["codecov"])["notify"]
        )
        self.assertEqual(notify["after_n_builds"], str(len(EXPECTED_FLAGS)))

    def test_statuses_are_informational_without_fabricated_thresholds(self):
        coverage = cast(dict[str, object], self.config["coverage"])
        statuses = cast(dict[str, dict[str, dict[str, object]]], coverage["status"])
        for kind in ("project", "patch"):
            check = statuses[kind]["default"]
            self.assertEqual(check["target"], "auto", kind)
            self.assertEqual(check["informational"], "true", kind)
        flagged = statuses["project"]
        self.assertEqual(set(flagged), {"default", *EXPECTED_FLAGS})
        for flag in EXPECTED_FLAGS:
            self.assertEqual(flagged[flag]["flags"], [flag])
            self.assertEqual(flagged[flag]["informational"], "true")
            self.assertNotIn("target", flagged[flag])
        self.assertNotIn("ignore", self.config)
        self.assertNotIn("range", coverage)

    def test_comment_requires_head_and_not_base(self):
        comment = cast(dict[str, str], self.config["comment"])
        self.assertEqual(comment["require_head"], "true")
        self.assertEqual(comment["require_base"], "false")
        self.assertEqual(comment["layout"], "diff, flags, files")

    def test_report_age_matches_artifact_retention(self):
        # Ручное одобрение окружения может задержать публикацию, поэтому срок
        # приёма отчётов Codecov обязан покрывать хранение артефактов CI.
        codecov = cast(dict[str, str], self.config["codecov"])
        retention = workflow_artifact_retention_days()
        self.assertEqual(codecov["max_report_age"], f"{retention}d")


class ReportDiagnosticsTests(unittest.TestCase):
    def test_raw_windows_report_is_rejected_until_normalized(self):
        # Фикстура хранится с LF по .gitattributes, поэтому CRLF собирается явно:
        # иначе проверка окончаний строк зависела бы от настроек checkout.
        stored = (FIXTURES / "python-lcov-windows-raw.lcov").read_bytes()
        raw = stored.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        self.assertIn(b"\r\n", raw)
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "lcov.info"
            _ = report.write_bytes(raw)
            rejected = run_script(
                PYTHON_VALIDATOR,
                "--report",
                str(report),
                "--platform",
                "windows",
                "--repo-root",
                ".",
            )
            self.assertEqual(rejected.returncode, 1, rejected.stdout)
            self.assertIn("не нормализован", rejected.stderr)
            normalized = run_script(
                NORMALIZER, "--report", str(report), "--repo-root", "."
            )
            self.assertEqual(normalized.returncode, 0, normalized.stderr)
            self.assertNotIn(b"\r", report.read_bytes())

    def test_empty_report_is_a_measurement_error_not_zero_percent(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "lcov.info"
            _ = report.write_bytes(b"")
            result = run_script(
                PYTHON_VALIDATOR,
                "--report",
                str(report),
                "--platform",
                "linux",
                "--repo-root",
                ".",
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("Отчёт пуст", result.stderr)

    def test_corrupt_dotnet_report_is_a_measurement_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            _ = shutil.copy(
                FIXTURES / "cobertura-corrupt.xml", directory / "dotnet.cobertura.xml"
            )
            result = run_script(
                DOTNET_VALIDATOR, str(directory), "unit", "--repo-root", "."
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("повреждённый отчёт", result.stderr)

    def test_missing_report_is_not_silently_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            missing = Path(temporary) / "absent.info"
            result = run_script(
                PYTHON_VALIDATOR,
                "--report",
                str(missing),
                "--platform",
                "linux",
                "--repo-root",
                ".",
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("Отчёт отсутствует", result.stderr)

    def test_corrupt_cobertura_is_rejected_by_path_normalizer(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "dotnet.cobertura.xml"
            _ = shutil.copy(FIXTURES / "cobertura-corrupt.xml", report)
            result = run_script(
                COBERTURA_NORMALIZER, "--report", str(report), "--repo-root", "."
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("Повреждённый отчёт", result.stderr)


if __name__ == "__main__":
    _ = unittest.main()
