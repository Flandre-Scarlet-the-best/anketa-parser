"""Доказать на настоящем прогоне атрибуцию дочерних процессов и ветвей coverage."""

from __future__ import annotations

import ast
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from typing import override

from coverage import CoverageData
from coverage.parser import PythonParser

PROBE = """\
def parent_path() -> str:
    return "parent"


def child_path() -> str:
    return "child"


def child_branch(value: int) -> str:
    if value > 0:
        return "positive"
    return "non-positive"


def never_path() -> str:
    return "never"
"""
CHILD = """\
import probe

print(probe.child_path())
print(probe.child_branch(1))
"""
ISOLATED = """\
def isolated_path() -> str:
    return "isolated"


print(isolated_path())
"""
RUNNER = """\
import subprocess
import sys
from pathlib import Path

import probe

here = Path(__file__).resolve().parent
print(probe.parent_path())
_ = subprocess.run([sys.executable, str(here / "child.py")], check=True)
# Интерпретатор без site не выполняет .pth, поэтому измерение не начинается.
isolated = subprocess.run(
    [sys.executable, "-S", str(here / "isolated.py")],
    check=True,
    capture_output=True,
    text=True,
)
print(isolated.stdout.strip())
"""
COVERAGERC = """\
[run]
branch = True
patch = subprocess
source = .
data_file = .coverage

[report]
show_missing = True
"""
TIMEOUT_SECONDS = 120


def function_body_lines(source: str, name: str) -> set[int]:
    """Строки тела функции без строки определения, найденные через ast."""
    module = ast.parse(source)
    for node in module.body:
        if isinstance(node, ast.FunctionDef) and node.name == name:
            lines: set[int] = set()
            for statement in node.body:
                for child in ast.walk(statement):
                    lineno = getattr(child, "lineno", None)
                    if isinstance(lineno, int):
                        lines.add(lineno)
            return lines
    raise AssertionError(f"В исходнике нет функции {name}")


def condition_line(source: str, name: str) -> int:
    """Строка первого ветвления в теле функции."""
    module = ast.parse(source)
    for node in module.body:
        if isinstance(node, ast.FunctionDef) and node.name == name:
            for statement in node.body:
                if isinstance(statement, ast.If):
                    return statement.lineno
    raise AssertionError(f"В исходнике нет ветвления в {name}")


class RuntimeCoverageTests(unittest.TestCase):
    """Инструментированный прогон в отдельном каталоге, изолированный от внешнего."""

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temp: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()

    @override
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name, text in (
            (".coveragerc", COVERAGERC),
            ("probe.py", PROBE),
            ("child.py", CHILD),
            ("isolated.py", ISOLATED),
            ("runner.py", RUNNER),
        ):
            _ = (self.root / name).write_text(text, encoding="utf-8")

    def nested_env(self) -> dict[str, str]:
        """Среда без внешних переменных покрытия: прогон инструментирует себя сам."""
        env = {
            name: value
            for name, value in os.environ.items()
            if not name.startswith("COVERAGE_")
        }
        env["PYTHONDONTWRITEBYTECODE"] = "1"
        return env

    def coverage_command(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        """Запустить coverage в каталоге прогона с изолированной средой."""
        return subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [sys.executable, "-m", "coverage", *arguments],
            cwd=self.root,
            env=self.nested_env(),
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=TIMEOUT_SECONDS,
            check=False,
        )

    def parallel_files(self) -> list[Path]:
        """Файлы данных отдельных процессов до объединения."""
        return sorted(self.root.glob(".coverage.*"))

    def measured_path(self, path: Path) -> str:
        """Ключ файла в объединённых данных покрытия."""
        data = CoverageData(basename=str(self.root / ".coverage"))
        data.read()
        for measured in data.measured_files():
            if Path(measured).resolve() == path.resolve():
                return measured
        raise AssertionError(f"Файл {path.name} не измерен")

    def measured_lines(self, path: Path) -> set[int]:
        """Строки файла, записанные в объединённые данные."""
        data = CoverageData(basename=str(self.root / ".coverage"))
        data.read()
        return set(data.lines(self.measured_path(path)) or ())

    def test_subprocess_child_is_attributed_and_dash_s_is_not(self):
        result = self.coverage_command("run", "runner.py")
        self.assertEqual(result.returncode, 0, result.stderr)
        collected = self.parallel_files()
        self.assertGreaterEqual(
            len(collected),
            2,
            "Дочерние процессы не создали отдельные файлы данных: "
            + ", ".join(path.name for path in collected),
        )
        time.sleep(1.0)
        self.assertEqual(
            len(self.parallel_files()),
            len(collected),
            "Данные дописывались после завершения прогона",
        )
        combined = self.coverage_command("combine")
        self.assertEqual(combined.returncode, 0, combined.stderr)
        self.assertEqual(self.parallel_files(), [], "combine оставил частичные файлы")

        probe = self.root / "probe.py"
        covered = self.measured_lines(probe)
        self.assertLessEqual(function_body_lines(PROBE, "parent_path"), covered)
        self.assertLessEqual(
            function_body_lines(PROBE, "child_path"),
            covered,
            "Тело, исполняемое только в дочернем процессе, не атрибутировано",
        )
        self.assertFalse(
            function_body_lines(PROBE, "never_path") & covered,
            "Неисполненная функция приписана как покрытая",
        )
        self.assertIn(
            "positive",
            result.stdout,
            "Дочерний процесс не исполнил ветвь: атрибуция исходов не доказана",
        )
        self.assertIn("isolated", result.stdout, "Процесс с -S не выполнился")
        isolated = self.root / "isolated.py"
        data = CoverageData(basename=str(self.root / ".coverage"))
        data.read()
        # Интерпретатор с -S не запускает автозапуск измерения, поэтому строк
        # этого процесса в данных нет (сам файл может быть отмечен как измеренный).
        self.assertEqual(
            self.measured_lines(isolated),
            set(),
            "Процесс с -S приписал строки к измерению",
        )
        self.assertTrue(data.has_arcs(), "Ветви не записаны в данные")
        branch_line = condition_line(PROBE, "child_branch")
        parser = PythonParser(text=PROBE, filename=str(probe))
        parser.parse_source()
        possible = {arc for arc in parser.arcs() if arc[0] == branch_line}
        executed = {
            arc
            for arc in (data.arcs(self.measured_path(probe)) or ())
            if arc[0] == branch_line
        }
        self.assertTrue(
            executed,
            "Исход ветви, исполненный в дочернем процессе, не атрибутирован",
        )
        self.assertTrue(
            possible - executed,
            "Неисполненный исход ветви не различим в объединённых данных",
        )


if __name__ == "__main__":
    _ = unittest.main()
