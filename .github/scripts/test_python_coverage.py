"""Проверка динамического охвата исходников BasedPyright."""

import json
import tempfile
import unittest
from pathlib import Path

from check_python_coverage import validate_coverage


class PythonCoverageTests(unittest.TestCase):
    def test_growth_is_allowed_and_missing_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            scripts = root / "scripts"
            scripts.mkdir()
            config = root / "pyrightconfig.json"
            _ = config.write_text(
                json.dumps({"include": ["scripts"]}), encoding="utf-8"
            )
            for index in range(1, 4):
                _ = (scripts / f"source_{index}.py").write_text(
                    "value: int = 1\n", encoding="utf-8"
                )
                self.assertEqual(
                    validate_coverage(
                        json.dumps({"summary": {"filesAnalyzed": index}}), config
                    ),
                    [],
                )
                self.assertTrue(
                    validate_coverage(
                        json.dumps({"summary": {"filesAnalyzed": index - 1}}), config
                    )
                )
            # Исключение не уменьшает ожидаемый охват и не скрывает потерю файла.
            _ = config.write_text(
                json.dumps(
                    {"include": ["scripts"], "exclude": ["scripts/source_3.py"]}
                ),
                encoding="utf-8",
            )
            self.assertTrue(
                validate_coverage(json.dumps({"summary": {"filesAnalyzed": 2}}), config)
            )

    def test_unsupported_config_and_invalid_report_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            config = Path(temporary) / "pyrightconfig.json"
            for content in ({}, {"include": ["missing"]}, {"include": ["**/*.py"]}):
                _ = config.write_text(json.dumps(content), encoding="utf-8")
                self.assertTrue(
                    validate_coverage('{"summary": {"filesAnalyzed": 0}}', config)
                )

    def test_required_sources_cannot_be_removed_from_include(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            scripts = root / "scripts"
            skills = root / "skills"
            scripts.mkdir()
            skills.mkdir()
            _ = (scripts / "check.py").write_text("pass", encoding="utf-8")
            _ = (skills / "helper.py").write_text("pass", encoding="utf-8")
            config = root / "pyrightconfig.json"
            _ = config.write_text(
                json.dumps({"include": ["scripts"]}), encoding="utf-8"
            )
            report = json.dumps({"summary": {"filesAnalyzed": 1}})
            self.assertTrue(validate_coverage(report, config, (skills,)))
