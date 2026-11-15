"""Приёмка языка инструкций, маршрутов и доверительной границы CI."""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from check_pr_body import REQUIRED, validate
from check_skill_contracts import validate_tree
from test_check_pr_body import valid_body

ROOT = Path(__file__).resolve().parents[2]


class AgentContractTests(unittest.TestCase):
    def test_english_instructions_keep_russian_contract_literals(self):
        skill = ROOT / ".agents/skills/git-workflow"
        files = [ROOT / "AGENTS.md", *skill.rglob("*.md")]
        self.assertEqual(len(files), 5)
        for path in files:
            text = path.read_text(encoding="utf-8")
            # Литералы контракта выделяются кодом; окружающая инструкция английская.
            prose = re.sub(r"```[^\n]*\n.*?```", "", text, flags=re.S)
            prose = re.sub(r"`[^`\n]+`", "", prose)
            self.assertIsNone(re.search(r"[А-Яа-яЁё]", prose), str(path))
        body_contract = (skill / "references/pr-body.md").read_text(encoding="utf-8")
        for heading in REQUIRED:
            self.assertIn("## " + heading, body_contract)
        for path in (ROOT / "AGENTS.md", skill / "SKILL.md"):
            self.assertIn("in Russian", path.read_text(encoding="utf-8"))
        self.assertEqual(validate(valid_body()), [])
        self.assertTrue(validate(valid_body().replace("## Цель", "## Goal")))

    def test_real_repository_skill_and_map_links(self):
        self.assertEqual(validate_tree(ROOT), [])

    def test_ci_covers_dependent_prs_without_weakening_dotnet(self):
        text = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertRegex(text, r"pull_request:\n  merge_group:")
        for command in (
            "dotnet restore --locked-mode",
            "dotnet format anketa-parser.slnx --verify-no-changes --no-restore",
            "dotnet build --no-restore -c Release",
            "dotnet test --no-build -c Release",
            "docker compose config --quiet",
            "python -m unittest discover",
            "python .github/scripts/check_skill_contracts.py",
        ):
            self.assertIn(command, text)

    def test_trusted_workflow_only_executes_base_validator(self):
        text = (ROOT / ".github/workflows/pr-body-trusted.yml").read_text(encoding="utf-8")
        self.assertIn("  pull_request_target:", text)
        self.assertNotIn("  pull_request:", text)
        self.assertEqual(text.count("uses: actions/checkout@"), 1)
        self.assertIn("ref: ${{ github.event.pull_request.base.sha }}", text)
        self.assertIn("persist-credentials: false", text)
        for forbidden in ("head.sha", "head.ref", "refs/pull", "secrets.", ": write", "write-all"):
            self.assertNotIn(forbidden, text)
        runs = re.findall(r"(?m)^\s+run: (.+)$", text)
        self.assertEqual(runs, ['python .github/scripts/check_pr_body.py --event-path "$GITHUB_EVENT_PATH"'])

    def test_replaced_head_validator_does_not_change_base_result(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            base, head = root / "base", root / "head"
            base.mkdir()
            head.mkdir()
            shutil.copy(ROOT / ".github/scripts/check_pr_body.py", base)
            (head / "check_pr_body.py").write_text("raise RuntimeError('Код head выполнен')", encoding="utf-8")
            event = root / "event.json"
            env = dict(os.environ, PYTHONPATH=str(head))
            for body, status in ((valid_body(), 0), ("## Цель\nНеполный текст", 1)):
                event.write_text(json.dumps({"pull_request": {"body": body}}), encoding="utf-8")
                result = subprocess.run(
                    [sys.executable, "check_pr_body.py", "--event-path", str(event)],
                    cwd=base, env=env, capture_output=True,
                )
                self.assertEqual(result.returncode, status, result.stderr)
                self.assertNotIn("Код head выполнен".encode(), result.stderr)

    def test_cli_errors_are_russian_and_no_review_is_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            body = Path(temporary) / "body.md"
            for content, status, message in (
                (valid_body(), 0, "Структура PR body соответствует контракту"),
                ("## Goal\nОписание", 1, "Отсутствует обязательный заголовок: ## Цель"),
            ):
                body.write_text(content, encoding="utf-8")
                result = subprocess.run(
                    [sys.executable, str(ROOT / ".github/scripts/check_pr_body.py"), "--body-file", str(body)],
                    capture_output=True, env=dict(os.environ, PYTHONIOENCODING="utf-8"),
                )
                self.assertEqual(result.returncode, status)
                self.assertIn(message, (result.stdout + result.stderr).decode("utf-8"))


if __name__ == "__main__":
    unittest.main()
