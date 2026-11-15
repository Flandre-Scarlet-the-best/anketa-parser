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


def instruction_prose(text: str) -> str:
    """Текст инструкции без литералов: fenced-блоки и код-спаны удаляются."""
    prose = re.sub(r"```[^\n]*\n.*?```", "", text, flags=re.S)
    return re.sub(r"`[^`\n]+`", "", prose)


def requires_russian_output(text: str) -> bool:
    """Требует ли инструкция русскоязычного результата.

    Проверяется наличие языка результата в прозе, а не конкретная английская
    конструкция: «Communicate ... in Russian», «Use Russian for ...», «must be
    Russian», «the result language is Russian» и любая равнозначная
    формулировка проходят одинаково.
    """
    return re.search(r"\bRussian\b", instruction_prose(text)) is not None


class AgentContractTests(unittest.TestCase):
    def test_english_instructions_keep_russian_contract_literals(self):
        """Языковой контракт инструкций, а не их текущий состав и формулировки.

        Набор документов определяется фактическим деревом, поэтому новый
        легитимный reference не ломает проверку; требования к маршрутам и
        русским литералам контракта сохраняются.
        """
        skill = ROOT / ".agents/skills/git-workflow"
        files = [ROOT / "AGENTS.md", *sorted((ROOT / ".agents/skills").rglob("*.md"))]
        self.assertTrue((skill / "SKILL.md").is_file(), "нужен сам канонический skill")
        for path in files:
            # Литералы контракта выделяются кодом; окружающая инструкция английская.
            prose = instruction_prose(path.read_text(encoding="utf-8"))
            self.assertIsNone(re.search(r"[А-Яа-яЁё]", prose), str(path))
        # Требование русскоязычного результата задаётся в прозе корневого
        # контракта и навыка. Проверяется сам язык, поэтому равноценная
        # переформулировка правила не ломает CI.
        for path in (ROOT / "AGENTS.md", *sorted((ROOT / ".agents/skills").glob("*/SKILL.md"))):
            self.assertTrue(requires_russian_output(path.read_text(encoding="utf-8")), str(path))
        # Владелец формата описания PR сохраняет все русские заголовки контракта.
        body_contract = (skill / "references/pr-body.md").read_text(encoding="utf-8")
        for heading in REQUIRED:
            self.assertIn("## " + heading, body_contract)
        # Обязательные маршруты навыка объявлены каноническим SKILL.md. Новый
        # reference может подключаться из другого документа, поэтому здесь
        # проверяются именно владельцы процедур, а не весь состав каталога.
        skill_text = (skill / "SKILL.md").read_text(encoding="utf-8")
        for route in ("publication.md", "pr-body.md", "pr-lifecycle.md"):
            self.assertIn(route, skill_text, route)
        self.assertEqual(validate(valid_body()), [])
        self.assertTrue(validate(valid_body().replace("## Цель", "## Goal")))

    def test_language_check_detects_real_violation(self):
        # Страховка: проверки действительно отлавливают нарушения контракта.
        self.assertIsNotNone(re.search(r"[А-Яа-яЁё]", instruction_prose("Обычный текст без литералов.")))
        self.assertFalse(requires_russian_output("Write everything in English."))

    def test_russian_output_requirement_is_phrasing_independent(self):
        # Равноценные формулировки постоянного контракта проходят одинаково:
        # тест не должен закреплять одну-две конкретные английские конструкции.
        for text in (
            "Communicate with the user in Russian.",
            "Use Russian for all user-facing communication.",
            "All user-facing output must be Russian.",
            "The result language is Russian.",
            "Write everything in Russian.",
            "Reports are authored in Russian.",
        ):
            with self.subTest(text=text):
                self.assertTrue(requires_russian_output(text))
        # Упоминание языка внутри литерала требованием не является.
        self.assertFalse(requires_russian_output("See `Russian` in the example."))
        self.assertFalse(requires_russian_output("```text\nRussian\n```"))

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
        self.assertEqual(runs, ['python .github/scripts/check_coderabbit_comment.py'])

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
                (valid_body(), 0, "Структура описания PR соответствует контракту"),
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
