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
    prose = re.sub(r"```[^\n]*\n.*?```", "", text, flags=re.DOTALL)
    return re.sub(r"`[^`\n]+`", "", prose)


def requires_russian_output(text: str) -> bool:
    """Проверить явную директиву; смысл произвольной прозы проверяется ревью."""
    return re.findall(
        r"(?m)^<!-- output-language: ([a-z-]+) -->$", instruction_prose(text)
    ) == ["ru"]


class AgentContractTests(unittest.TestCase):
    def test_public_context_is_addable_while_tool_state_stays_private(self):
        public = (".codex/context/INDEX.md", ".codex/context/maps/figure.svg")
        private = (
            ".codex/local/pr/example/body.md",
            ".codex/config.toml",
            ".codex/cache/state.json",
            ".serena/memories/example.md",
        )
        # --no-index проверяет сами ignore-правила и после добавления context.
        result = subprocess.run(  # noqa: S603
            ["git", "check-ignore", "--no-index", *public, *private],  # noqa: S607
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(set(result.stdout.splitlines()), set(private))

    def test_english_instructions_keep_russian_contract_literals(self):
        """Языковой контракт инструкций, а не их текущий состав и формулировки.

        Набор документов определяется фактическим деревом, поэтому новый
        легитимный reference не ломает проверку; требования к маршрутам и
        русским литералам контракта сохраняются.
        """
        skill = ROOT / ".agents/skills/git-workflow"
        files = [
            ROOT / "AGENTS.md",
            *sorted((ROOT / ".agents/skills").rglob("*.md")),
            *sorted((ROOT / ".codex/context").rglob("*.md")),
        ]
        self.assertTrue((skill / "SKILL.md").is_file(), "нужен сам канонический skill")
        for path in files:
            # Литералы контракта выделяются кодом; окружающая инструкция английская.
            prose = instruction_prose(path.read_text(encoding="utf-8"))
            self.assertIsNone(re.search(r"[А-Яа-яЁё]", prose), str(path))
        # Язык задаётся явной директивой корневого контракта и навыка;
        # проверка не интерпретирует английскую прозу, поэтому равноценная
        # переформулировка правила не ломает CI.
        for path in (
            ROOT / "AGENTS.md",
            *sorted((ROOT / ".agents/skills").glob("*/SKILL.md")),
        ):
            self.assertTrue(
                requires_russian_output(path.read_text(encoding="utf-8")), str(path)
            )
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
        self.assertIsNotNone(
            re.search(r"[А-Яа-яЁё]", instruction_prose("Обычный текст без литералов."))
        )
        self.assertFalse(requires_russian_output("Write everything in English."))

    def test_language_directive_is_independent_of_prose(self):
        for prose in (
            "Communicate in Russian.",
            "Reports use Russian.",
            "Reworded prose.\n",
        ):
            self.assertTrue(
                requires_russian_output(prose + "\n<!-- output-language: ru -->")
            )
        for text in (
            "Do not write in Russian.",
            "Russian output is forbidden.",
            "See `Russian`.",
            "```text\nRussian\n```",
            "```text\n<!-- output-language: ru -->\n```",
            "<!-- output-language: en -->",
            "<!-- output-language: ru -->\n<!-- output-language: en -->",
        ):
            with self.subTest(text=text):
                self.assertFalse(requires_russian_output(text))

    def test_real_repository_skill_and_map_links(self):
        self.assertEqual(validate_tree(ROOT), [])

    def test_ci_covers_dependent_prs_without_weakening_dotnet(self):
        text = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertRegex(text, r"pull_request:\n  merge_group:")
        for command in (
            "dotnet restore --locked-mode",
            "dotnet format anketa-parser.slnx --verify-no-changes --no-restore",
            "dotnet build --no-restore -c Release",
            "dotnet test --project tests/AnketaParser.Tests/AnketaParser.Tests.csproj -c Release --no-build",
            "dotnet test --project tests/AnketaParser.DockerTests/AnketaParser.DockerTests.csproj -c Release --no-build",
            "docker compose config --quiet",
            "python -m coverage run -m unittest discover -s .github/scripts -p 'test_*.py'",
            "python -m coverage run -m unittest discover -s .github/scripts -p test_background_wait.py -v",
            "python .github/scripts/check_skill_contracts.py",
            "python .github/scripts/check_markdown_links.py",
        ):
            self.assertIn(command, text)

    def test_actions_use_verified_node24_releases_and_coverage_guard(self):
        expected = {
            "actions/checkout": "3d3c42e5aac5ba805825da76410c181273ba90b1",
            "actions/setup-python": "6d4fd22b20465ccc0f7354476588e8df7c9ca0e0",
        }
        for filename in ("ci.yml", "pr-body.yml", "pr-body-trusted.yml"):
            text = (ROOT / ".github/workflows" / filename).read_text(encoding="utf-8")
            for action, sha in expected.items():
                refs = re.findall(re.escape(action) + r"@([^\s]+)", text)
                self.assertTrue(refs, (filename, action))
                self.assertEqual(set(refs), {sha}, (filename, action))
        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertEqual(
            re.findall(r"actions/setup-dotnet@([^\s]+)", ci),
            ["0f4a354dd5547a28c70b7853dbd95d5b67886e73"],
        )
        self.assertIn(
            'python .github/scripts/check_python_coverage.py "$RUNNER_TEMP/basedpyright-report.json" .github/pyrightconfig.json',
            ci,
        )
        trusted = (ROOT / ".github/workflows/pr-body-trusted.yml").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("allow-unsafe-pr-checkout:", trusted)

    def test_trusted_workflow_only_executes_base_validator(self):
        text = (ROOT / ".github/workflows/pr-body-trusted.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("  pull_request_target:", text)
        self.assertNotIn("  pull_request:", text)
        self.assertEqual(text.count("uses: actions/checkout@"), 1)
        self.assertIn("ref: ${{ github.event.pull_request.base.sha }}", text)
        self.assertIn("persist-credentials: false", text)
        for forbidden in (
            "head.sha",
            "head.ref",
            "refs/pull",
            "secrets.",
            ": write",
            "write-all",
        ):
            self.assertNotIn(forbidden, text)
        runs = re.findall(r"(?m)^\s+run: (.+)$", text)
        self.assertEqual(runs, ["python .github/scripts/check_coderabbit_comment.py"])

    def test_event_path_cli_compatibility_ignores_pythonpath_head(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            base, head = root / "base", root / "head"
            base.mkdir()
            head.mkdir()
            _ = shutil.copy(ROOT / ".github/scripts/check_pr_body.py", base)
            _ = (head / "check_pr_body.py").write_text(
                "raise RuntimeError('Код head выполнен')", encoding="utf-8"
            )
            event = root / "event.json"
            env = dict(os.environ, PYTHONPATH=str(head))
            for body, status in ((valid_body(), 0), ("## Цель\nНеполный текст", 1)):
                _ = event.write_text(
                    json.dumps({"pull_request": {"body": body}}), encoding="utf-8"
                )
                # Ожидаемое поведение CLI проверяется по коду возврата, поэтому
                # исключение на ненулевом коде здесь не нужно. Команда —
                # литеральный список из sys.executable и пути файла теста.
                result = subprocess.run(  # noqa: S603
                    [sys.executable, "check_pr_body.py", "--event-path", str(event)],
                    cwd=base,
                    env=env,
                    capture_output=True,
                    check=False,
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
                _ = body.write_text(content, encoding="utf-8")
                # Ненулевой код возврата — ожидаемый результат проверки.
                # Аргументы — путь этого же файла в репозитории, без shell.
                result = subprocess.run(  # noqa: S603
                    [
                        sys.executable,
                        str(ROOT / ".github/scripts/check_pr_body.py"),
                        "--body-file",
                        str(body),
                    ],
                    capture_output=True,
                    env=dict(os.environ, PYTHONIOENCODING="utf-8"),
                    check=False,
                )
                self.assertEqual(result.returncode, status)
                self.assertIn(message, (result.stdout + result.stderr).decode("utf-8"))


if __name__ == "__main__":
    _ = unittest.main()
