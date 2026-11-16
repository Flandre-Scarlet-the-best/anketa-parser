"""Статические контракты инструкций; настоящий CLI и обновление не запускаются."""

import fnmatch
import json
import re
import unittest
from pathlib import Path
from typing import ClassVar, NotRequired, TypedDict, cast, override

import yaml

ROOT = Path(__file__).resolve().parents[2]
SKILL = ROOT / ".agents/skills/coderabbit-review"


class AutoReview(TypedDict):
    enabled: bool
    auto_incremental_review: bool


class FinishingTouch(TypedDict):
    enabled: bool


class PathInstruction(TypedDict):
    path: str


class Reviews(TypedDict):
    profile: str
    auto_review: AutoReview
    review_status: bool
    finishing_touches: dict[str, FinishingTouch]
    path_instructions: list[PathInstruction]
    tools: NotRequired[dict[str, object]]


class GuidelineFile(TypedDict):
    files: str


class CodeGuidelines(TypedDict):
    filePatterns: list[GuidelineFile]


class KnowledgeBase(TypedDict):
    code_guidelines: CodeGuidelines


class ProviderConfig(TypedDict):
    """Проверяемые разделы .coderabbit.yaml; прочие ключи тест не читает."""

    language: str
    reviews: Reviews
    knowledge_base: KnowledgeBase


class WorkflowStep(TypedDict, total=False):
    run: str
    env: dict[str, str]


class WorkflowJob(TypedDict):
    steps: list[WorkflowStep]


class Workflow(TypedDict):
    """Проверяемые разделы workflow; ключ on намеренно остаётся строкой."""

    on: dict[str, object]
    permissions: dict[str, str]
    jobs: dict[str, WorkflowJob]


def read_workflow(path: Path) -> Workflow:
    """Прочитать workflow безопасным BaseLoader, сохранив ключ on строкой.

    В описаниях типов PyYAML результат load имеет тип Any; структура проверяется
    утверждениями теста, поэтому тип уточняется один раз здесь.
    """
    return cast(
        Workflow,
        yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader),  # noqa: S506
    )


def expand_braces(pattern: str) -> list[str]:
    """Раскрыть альтернативы в glob, не обращаясь к файловой системе."""
    match = re.search(r"\{([^{}]+)\}", pattern)
    if match is None:
        return [pattern]
    return [
        expanded
        for option in match[1].split(",")
        for expanded in expand_braces(
            pattern[: match.start()] + option + pattern[match.end() :]
        )
    ]


def review_contract(text: str) -> object:
    """Прочитать только явный контракт; согласованность прозы проверяется ревью."""
    blocks: list[str] = re.findall(
        r"```json review-contract\s*\n(.*?)\n```", text, re.DOTALL
    )
    if len(blocks) != 1:
        raise ValueError("Ожидается единственный структурированный контракт ревью")
    return cast(object, json.loads(blocks[0]))


class ReviewInstructionTests(unittest.TestCase):
    def test_declared_authorization_update_and_completion_invariants(self):
        text = (SKILL / "references/review-workflow.md").read_text(encoding="utf-8")
        expected = {
            "authorization": "explicit-positive-user-request",
            "deep": "explicit-intent",
            "paid": "separate-explicit-consent",
            "preflight_order": [
                "resolve-executable",
                "lookup-latest-stable",
                "update-if-outdated",
                "verify-installed-version",
                "discover-interface",
                "review",
            ],
            "latest_stable_url": "https://cli.coderabbit.ai/releases/latest/VERSION",
            "update": {
                "current": "skip-reinstall",
                "outdated": "automatic-supported-unattended",
                "windows": "verify-authenticode",
                "failure": "block-before-discovery",
                "other-tools": "no-update",
            },
            "completion": [
                "full-stream-to-eof",
                "successful-terminal-outcome",
                "full-requested-file-coverage",
                "no-errors-or-contradictions",
                "successful-exit",
                "fresh-review-evidence",
            ],
            "incomplete": [
                "review_skipped",
                "too_many_files",
                "unreviewedFileCount",
                "reused: true",
            ],
            "incomplete_credit": 0,
            "incomplete_marker": False,
            "fresh_retry": "same-mode-base-scope-focus",
            "automatic_clear": False,
            "default_iterations": 3,
            "explicit_count_overrides_default": True,
            "clean_shortens_cycle": False,
            "clean_marker_commit": True,
            "iteration_transition": {
                "credit": "after-publication-readback",
                "next_head": "previous-published-commit-different-from-reviewed-head",
                "retry_credit": 0,
                "resume": "reconcile-last-credited-iteration",
                "past_gap": "stop-disclose-no-retroactive-marker",
            },
            "base": "actual-direct-pr-base",
            "publication_owner": "git-workflow",
        }
        self.assertEqual(review_contract(text), expected)
        # Переносы и редакция окружающей прозы не меняют технические инварианты.
        declaration = text[text.index("```json review-contract") :]
        self.assertEqual(
            review_contract("Reworded instructions.\n" + declaration), expected
        )
        for field, value in (
            ("authorization", "automatic"),
            ("incomplete_credit", 1),
            ("incomplete_marker", True),
            ("automatic_clear", True),
        ):
            altered = dict(expected)
            altered[field] = value
            self.assertNotEqual(
                review_contract(
                    "```json review-contract\n" + json.dumps(altered) + "\n```"
                ),
                expected,
            )
        self.assertFalse(
            (SKILL / "scripts").exists(), "Навык не должен становиться обёрткой CLI"
        )

    def test_missing_or_duplicate_contract_is_rejected(self):
        for text in ("Do not review.", "```json review-contract\n{}\n```\n" * 2):
            with self.assertRaises(ValueError):
                _ = review_contract(text)


class ProviderConfigurationTests(unittest.TestCase):
    config: ClassVar[ProviderConfig]

    @classmethod
    @override
    def setUpClass(cls) -> None:
        cls.config = cast(
            ProviderConfig,
            yaml.safe_load((ROOT / ".coderabbit.yaml").read_text(encoding="utf-8")),
        )

    def test_russian_manual_review_and_no_automatic_mutations(self):
        self.assertEqual(self.config["language"], "ru-RU")
        reviews = self.config["reviews"]
        self.assertEqual(reviews["profile"], "assertive")
        self.assertIs(reviews["auto_review"]["enabled"], False)
        self.assertIs(reviews["auto_review"]["auto_incremental_review"], False)
        self.assertIs(reviews["review_status"], False)
        self.assertTrue(
            all(
                touch["enabled"] is False
                for touch in reviews["finishing_touches"].values()
            )
        )
        self.assertFalse(
            set(reviews.get("tools", {})) & {"clippy", "csharp", "dotnet", "roslyn"}
        )
        for guideline in self.config["knowledge_base"]["code_guidelines"][
            "filePatterns"
        ]:
            self.assertTrue((ROOT / guideline["files"]).is_file(), guideline)

    def test_each_path_rule_matches_real_owned_paths(self):
        paths = [
            path.relative_to(ROOT).as_posix()
            for directory in (
                "src",
                "tests",
                ".github",
                ".agents",
                "docs",
                ".codex/context",
            )
            for path in (ROOT / directory).rglob("*")
            if path.is_file() and not {"bin", "obj", "__pycache__"} & set(path.parts)
        ]
        paths += [path.name for path in ROOT.iterdir() if path.is_file()]
        for rule in self.config["reviews"]["path_instructions"]:
            patterns = expand_braces(rule["path"])
            # **/ также допускает ноль каталогов в glob провайдера.
            patterns += [p[3:] for p in patterns if p.startswith("**/")]
            with self.subTest(pattern=rule["path"]):
                self.assertTrue(
                    any(
                        fnmatch.fnmatchcase(path, pattern)
                        for path in paths
                        for pattern in patterns
                    )
                )

    def test_candidate_and_trusted_invoke_live_validator_with_read_only_token(self):
        for filename, event in (
            ("pr-body.yml", "pull_request"),
            ("pr-body-trusted.yml", "pull_request_target"),
        ):
            # BaseLoader сохраняет ключ on строкой вместо YAML 1.1 boolean и тоже
            # собирает только базовые типы: workflow читается из самого репозитория,
            # а не из данных PR. Замена на safe_load превратила бы on в True и
            # сломала бы проверку контракта события.
            workflow = read_workflow(ROOT / ".github/workflows" / filename)
            with self.subTest(workflow=filename):
                self.assertEqual(set(workflow["on"]), {event})
                self.assertEqual(
                    workflow["permissions"],
                    {"contents": "read", "pull-requests": "read"},
                )
                steps = next(iter(workflow["jobs"].values()))["steps"]
                runs = [step for step in steps if "run" in step]
                self.assertEqual(
                    [step["run"] for step in runs],
                    ["python .github/scripts/check_coderabbit_comment.py"],
                )
                self.assertEqual(
                    runs[0].get("env"),
                    {
                        "PR_NUMBER": "${{ github.event.pull_request.number }}",
                        "GITHUB_TOKEN": "${{ github.token }}",
                    },
                )
