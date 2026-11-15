"""Статические контракты инструкций; настоящий CLI и обновление не запускаются."""

import fnmatch
import re
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
SKILL = ROOT / ".agents/skills/coderabbit-review"


def expand_braces(pattern):
    """Раскрыть альтернативы в glob, не обращаясь к файловой системе."""
    match = re.search(r"\{([^{}]+)\}", pattern)
    if match is None:
        return [pattern]
    return [expanded for option in match[1].split(",")
            for expanded in expand_braces(pattern[:match.start()] + option + pattern[match.end():])]


class ReviewInstructionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        cls.workflow = (SKILL / "references/review-workflow.md").read_text(encoding="utf-8")
        cls.preflight = cls.workflow.split("## 3. Mandatory latest-stable preflight\n", 1)[1].split("## 4.", 1)[0]

    def test_outdated_cli_requires_unattended_update_then_version_proof_and_discovery(self):
        # Закрепляется порядок ворот инструкции, не работа внешнего updater.
        stages = ["1. Resolve the real executable", "2. Read the latest stable version",
                  "3. If the installed executable", "4. On Windows", "5. Resolve executable paths again",
                  "6. If release lookup"]
        positions = [self.preflight.index(stage) for stage in stages]
        self.assertEqual(positions, sorted(positions))
        for requirement in ("automatically update without asking", "https://cli.coderabbit.ai/releases/latest/VERSION",
                            "Homebrew-managed installations", "brew upgrade coderabbit",
                            "Authenticode", "Do not bypass signature", "both command"):
            self.assertIn(requirement, self.preflight)
        self.assertIn("Only after confirmed latest stable proceed to section 4", self.preflight)
        self.assertLess(self.workflow.index("## 4. Discover"), self.workflow.index("### 5.1 Invocation"))

    def test_current_cli_proves_latest_and_skips_reinstallation(self):
        self.assertIn("already matches confirmed latest stable, skip\n   reinstalling it", self.preflight)
        self.assertIn("before every separately requested cycle", self.preflight)
        self.assertIn("actual installed executable's\n   version", self.preflight)
        self.assertIn("No unattended update\nof any other tool", self.preflight)

    def test_unconfirmed_update_blocks_before_discovery_review_or_marker(self):
        for requirement in ("report a blocker before interface discovery or review",
                            "Do not continue\n   on an unconfirmed version",
                            "An updater's\n   exit code or promise to update does not prove replacement succeeded",
                            "zero completed iterations and no marker commit"):
            self.assertIn(requirement, self.preflight)

    def test_explicit_activation_iteration_and_completion_boundaries(self):
        for requirement in ("explicit_positive_user_count ?? 3", "Three is the default, not a maximum",
                            "clean pass does not shorten", "meaningful empty marker commit",
                            "Never create marker commits for them", "creating or testing this skill does not authorize"):
            self.assertIn(requirement, self.skill + self.workflow)
        for outcome in ("review_skipped", "too_many_files", "unreviewedFileCount", "reused: true",
                        "confirmed", "partially confirmed", "stale/repeated", "false positive"):
            self.assertIn(outcome, self.workflow)
        self.assertIn("actual direct base", self.workflow)
        self.assertIn("Deep requires explicit deep intent", self.skill)
        self.assertIn("Paid usage requires separate explicit consent", self.skill)
        self.assertIn("Never call `--clear` automatically", self.workflow)
        self.assertIn("git-workflow", self.skill)
        self.assertFalse((SKILL / "scripts").exists(), "цикл не должен становиться CLI-wrapper")


class ProviderConfigurationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = yaml.safe_load((ROOT / ".coderabbit.yaml").read_text(encoding="utf-8"))

    def test_russian_manual_review_and_no_automatic_mutations(self):
        self.assertEqual(self.config["language"], "ru-RU")
        reviews = self.config["reviews"]
        self.assertEqual(reviews["profile"], "assertive")
        self.assertIs(reviews["auto_review"]["enabled"], False)
        self.assertIs(reviews["auto_review"]["auto_incremental_review"], False)
        self.assertIs(reviews["review_status"], False)
        self.assertTrue(all(touch["enabled"] is False for touch in reviews["finishing_touches"].values()))
        self.assertFalse(set(reviews.get("tools", {})) & {"clippy", "csharp", "dotnet", "roslyn"})
        for guideline in self.config["knowledge_base"]["code_guidelines"]["filePatterns"]:
            self.assertTrue((ROOT / guideline["files"]).is_file(), guideline)

    def test_each_path_rule_matches_real_owned_paths(self):
        paths = [path.relative_to(ROOT).as_posix() for directory in ("src", "tests", ".github", ".agents")
                 for path in (ROOT / directory).rglob("*")
                 if path.is_file() and not {"bin", "obj", "__pycache__"} & set(path.parts)]
        paths += [path.name for path in ROOT.iterdir() if path.is_file()]
        for rule in self.config["reviews"]["path_instructions"]:
            patterns = expand_braces(rule["path"])
            # **/ также допускает ноль каталогов в glob провайдера.
            patterns += [p[3:] for p in patterns if p.startswith("**/")]
            with self.subTest(pattern=rule["path"]):
                self.assertTrue(any(fnmatch.fnmatchcase(path, pattern) for path in paths for pattern in patterns))

    def test_candidate_and_trusted_invoke_live_validator_with_read_only_token(self):
        for filename, event in (("pr-body.yml", "pull_request"), ("pr-body-trusted.yml", "pull_request_target")):
            # BaseLoader сохраняет ключ on строкой вместо YAML 1.1 boolean.
            workflow = yaml.load((ROOT / ".github/workflows" / filename).read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
            with self.subTest(workflow=filename):
                self.assertEqual(set(workflow["on"]), {event})
                self.assertEqual(workflow["permissions"], {"contents": "read", "pull-requests": "read"})
                steps = next(iter(workflow["jobs"].values()))["steps"]
                runs = [step for step in steps if "run" in step]
                self.assertEqual([step["run"] for step in runs], ["python .github/scripts/check_coderabbit_comment.py"])
                self.assertEqual(runs[0]["env"], {"PR_NUMBER": "${{ github.event.pull_request.number }}", "GITHUB_TOKEN": "${{ github.token }}"})
