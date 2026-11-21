"""Регрессионные сценарии проверки отчёта покрытия .NET."""

import math
import tempfile
import time
import unittest
from pathlib import Path
from typing import override
from xml.etree import ElementTree

from check_dotnet_coverage import validate

CORE = "AnketaParser.Core"
INFRA = "AnketaParser.Infrastructure"
HOST = "AnketaParser.Host"
CLI = "AnketaParser.Cli"
ASSEMBLIES = (CORE, INFRA, HOST, CLI)
DOCKER_PROJECT = "tests/AnketaParser.DockerTests/AnketaParser.DockerTests.csproj"


def truncate(covered: int, total: int) -> float:
    """Повторить усечение процента, которое выполняет coverlet."""
    return 1.0 if total == 0 else math.floor(covered / total * 10000) / 10000


def make_report(
    entries: list[tuple[str, str, dict[int, int]]],
    *,
    sources: tuple[str, ...] = (),
    branches: tuple[int, int] = (10, 6),
    line_rate: float | None = None,
    branch_rate: float | None = None,
    timestamp: int | None = None,
    root_tag: str = "coverage",
) -> str:
    """Собрать Cobertura-отчёт с метриками, пересчитанными как в coverlet."""
    lines_valid = sum(len(hits) for _, _, hits in entries)
    lines_covered = sum(
        sum(1 for value in hits.values() if value > 0) for _, _, hits in entries
    )
    branches_valid, branches_covered = branches
    attributes = {
        "line-rate": str(
            line_rate if line_rate is not None else truncate(lines_covered, lines_valid)
        ),
        "branch-rate": str(
            branch_rate
            if branch_rate is not None
            else truncate(branches_covered, branches_valid)
        ),
        "version": "1.9",
        "lines-valid": str(lines_valid),
        "lines-covered": str(lines_covered),
        "branches-valid": str(branches_valid),
        "branches-covered": str(branches_covered),
    }
    if timestamp is not None:
        attributes["timestamp"] = str(timestamp)
    coverage = ElementTree.Element(root_tag, attributes)
    if sources:
        sources_element = ElementTree.SubElement(coverage, "sources")
        for source in sources:
            source_element = ElementTree.SubElement(sources_element, "source")
            source_element.text = source
    packages_element = ElementTree.SubElement(coverage, "packages")
    for name in dict.fromkeys(entry[0] for entry in entries):
        package = ElementTree.SubElement(packages_element, "package", {"name": name})
        classes = ElementTree.SubElement(package, "classes")
        for package_name, filename, hits in entries:
            if package_name != name:
                continue
            element = ElementTree.SubElement(
                classes,
                "class",
                {"name": f"{name}.Type{len(classes)}", "filename": filename},
            )
            lines = ElementTree.SubElement(element, "lines")
            for number, value in sorted(hits.items()):
                _ = ElementTree.SubElement(
                    lines, "line", {"number": str(number), "hits": str(value)}
                )
    return ElementTree.tostring(coverage, encoding="unicode")


def write_project(path: Path, assembly: str, references: tuple[str, ...] = ()) -> None:
    """Создать минимальный проект с явным AssemblyName и ссылками."""
    project = ElementTree.Element("Project")
    properties = ElementTree.SubElement(project, "PropertyGroup")
    assembly_element = ElementTree.SubElement(properties, "AssemblyName")
    assembly_element.text = assembly
    items = ElementTree.SubElement(project, "ItemGroup")
    for name in references:
        _ = ElementTree.SubElement(
            items, "ProjectReference", {"Include": f"..\\{name}\\{name}.csproj"}
        )
    _ = path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text(
        ElementTree.tostring(project, encoding="unicode"), encoding="utf-8"
    )


class DotnetCoverageTests(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.repo: Path = Path()
        self.reports: Path = Path()
        self.sources: tuple[str, ...] = ()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name).resolve()
        for assembly in ASSEMBLIES:
            references = (CORE,) if assembly == INFRA else ()
            write_project(
                self.repo / "src" / assembly / f"{assembly}.csproj",
                assembly,
                references=references,
            )
            # Три строки исходника: отчёт фикстуры ссылается на строки 1 и 2.
            _ = (self.repo / "src" / assembly / "Source.cs").write_text(
                "class Source\n{\n}\n", encoding="utf-8"
            )
        write_project(
            self.repo / DOCKER_PROJECT,
            "AnketaParser.DockerTests",
            references=(INFRA,),
        )
        self.reports = self.repo / "TestResults" / "dotnet-unit"
        _ = self.reports.mkdir(parents=True)
        self.sources = (f"{self.repo.as_posix()}/src/",)

    def write(self, text: str, name: str = "coverage.cobertura.xml") -> None:
        _ = (self.reports / name).write_text(text, encoding="utf-8")

    def entries(self) -> list[tuple[str, str, dict[int, int]]]:
        return [
            (CORE, "AnketaParser.Core/Source.cs", {1: 3, 2: 0}),
            (INFRA, "AnketaParser.Infrastructure/Source.cs", {1: 1}),
            (HOST, "AnketaParser.Host/Source.cs", {1: 0}),
            (CLI, "AnketaParser.Cli/Source.cs", {1: 2}),
        ]

    def report(
        self,
        *,
        line_rate: float | None = None,
        branch_rate: float | None = None,
        timestamp: int | None = None,
        root_tag: str = "coverage",
        brackets: tuple[int, int] = (10, 6),
    ) -> str:
        moment = int(time.time()) if timestamp is None else timestamp
        return make_report(
            self.entries(),
            sources=self.sources,
            branches=brackets,
            line_rate=line_rate,
            branch_rate=branch_rate,
            timestamp=moment,
            root_tag=root_tag,
        )

    def test_valid_report_passes_and_keeps_zero_coverage_class(self):
        self.write(self.report())
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )
        # Настоящие 0% допустимы: класс без попаданий не превращает отчёт в ошибку.
        self.write(self.report(), "coverage.cobertura.xml")
        self.assertIn("AnketaParser.Host/Source.cs", self.report())

    def test_absolute_filename_inside_repository_is_accepted(self):
        absolute = f"{self.repo.as_posix()}/src/AnketaParser.Core/Source.cs"
        entries = [(CORE, absolute, {1: 1})]
        for assembly in (INFRA, HOST, CLI):
            entries.append((assembly, f"{assembly}/Source.cs", {1: 1}))
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )

    def test_missing_and_multiple_reports_are_rejected(self):
        self.assertIn(
            "нет ни одного *.xml", " ".join(validate(self.reports, "unit", self.repo))
        )
        self.write(self.report(), "first.xml")
        self.write(self.report(), "second.xml")
        errors = " ".join(validate(self.reports, "unit", self.repo))
        self.assertIn("ожидался один отчёт", errors)
        self.assertIn("first.xml", errors)

    def test_corrupt_report_and_foreign_root_are_rejected(self):
        self.write("<coverage line-rate=")
        self.assertIn(
            "повреждённый отчёт", " ".join(validate(self.reports, "unit", self.repo))
        )
        self.write(self.report(root_tag="report"))
        self.assertIn(
            "корневой элемент", " ".join(validate(self.reports, "unit", self.repo))
        )

    def test_missing_and_test_assemblies_are_rejected(self):
        entries = [entry for entry in self.entries() if entry[0] != HOST]
        entries.append(("AnketaParser.Tests", "AnketaParser.Tests/Source.cs", {1: 1}))
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("тестовые сборки: AnketaParser.Tests", errors)
        self.assertIn("отсутствуют production-сборки: AnketaParser.Host", errors)

    def test_paths_outside_src_and_build_artifacts_are_rejected(self):
        entries = self.entries()
        entries.append((CLI, "/outside/Source.cs", {1: 1}))
        entries.append((CLI, "AnketaParser.Cli/obj/Release/Source.cs", {1: 1}))
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("пути вне src репозитория", errors)
        self.assertIn("/outside/Source.cs", errors)
        self.assertIn("пути артефактов сборки", errors)

    def test_missing_source_file_is_rejected(self):
        entries = self.entries()
        entries.append((CLI, "AnketaParser.Cli/Absent.cs", {1: 1}))
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("отсутствующие файлы", errors)

    def test_empty_measurement_and_missing_branches_are_rejected(self):
        empty = [(assembly, f"{assembly}/Source.cs", {1: 0}) for assembly in ASSEMBLIES]
        self.write(make_report(empty, sources=self.sources, timestamp=int(time.time())))
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("ни одна строка не покрыта", errors)
        self.write(self.report(brackets=(0, 0)))
        self.assertIn(
            "нет данных о ветвях",
            " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0)),
        )

    def test_inconsistent_rates_are_rejected(self):
        self.write(self.report(line_rate=0.99))
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("line-rate=0.99", errors)
        self.assertIn("пересчёта", errors)
        self.write(self.report(branch_rate=0.0))
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("branch-rate=0", errors)

    def test_stale_report_is_rejected(self):
        self.write(self.report(timestamp=int(time.time()) - 48 * 3600))
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("устарел", errors)
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=None), []
        )

    def test_docker_flavour_requires_persistence_evidence(self):
        entries = [
            (CORE, "AnketaParser.Core/Source.cs", {1: 0}),
            (INFRA, "AnketaParser.Infrastructure/Source.cs", {1: 1}),
        ]
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        errors = " ".join(
            validate(self.reports, "docker", self.repo, max_age_hours=24.0)
        )
        self.assertIn("Docker-отчёт не подтверждает", errors)
        _ = (self.repo / "src" / INFRA / "Persistence").mkdir(parents=True)
        _ = (self.repo / "src" / INFRA / "Persistence" / "Store.cs").write_text(
            "class Store {}\n", encoding="utf-8"
        )
        entries[1] = (INFRA, "AnketaParser.Infrastructure/Persistence/Store.cs", {1: 4})
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        self.assertEqual(
            validate(self.reports, "docker", self.repo, max_age_hours=24.0), []
        )

    def test_unknown_flavour_is_rejected(self):
        self.assertIn(
            "неизвестный flavour", " ".join(validate(self.reports, "legacy", self.repo))
        )

    def test_absolute_paths_without_sources_are_accepted(self):
        base = f"{self.repo.as_posix()}/src"
        entries = [
            (assembly, f"{base}/{assembly}/Source.cs", {1: 2})
            for assembly in ASSEMBLIES
        ]
        self.write(make_report(entries, timestamp=int(time.time())))
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )

    def test_relative_paths_without_sources_are_rejected(self):
        self.write(make_report(self.entries(), sources=(), timestamp=int(time.time())))
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("<sources>", errors)

    def test_missing_entry_point_is_rejected(self):
        program = self.repo / "src" / CLI / "Program.cs"
        _ = program.write_text(
            "using System;\n\nConsole.WriteLine(1);\nreturn 0;\n", encoding="utf-8"
        )
        self.write(self.report())
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("рукописные точки входа исключены", errors)
        self.assertIn("src/AnketaParser.Cli/Program.cs", errors)
        entries = self.entries()
        entries.append((CLI, "AnketaParser.Cli/Program.cs", {2: 1, 3: 1}))
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )

    def test_entry_point_is_not_required_for_unmeasured_assembly(self):
        _ = (self.repo / "src" / HOST / "Program.cs").write_text(
            "using System;\n\nreturn 0;\n", encoding="utf-8"
        )
        entries = [
            (CORE, "AnketaParser.Core/Source.cs", {1: 0}),
            (INFRA, "AnketaParser.Infrastructure/Persistence/Store.cs", {1: 3}),
        ]
        _ = (self.repo / "src" / INFRA / "Persistence").mkdir(parents=True)
        _ = (self.repo / "src" / INFRA / "Persistence" / "Store.cs").write_text(
            "class Store {}\n", encoding="utf-8"
        )
        self.write(
            make_report(entries, sources=self.sources, timestamp=int(time.time()))
        )
        self.assertEqual(
            validate(self.reports, "docker", self.repo, max_age_hours=24.0), []
        )

    def test_exact_rate_is_accepted_and_inflation_is_rejected(self):
        entries: list[tuple[str, str, dict[int, int]]] = [
            (CORE, "AnketaParser.Core/Source.cs", {1: 1}),
            (INFRA, "AnketaParser.Infrastructure/Source.cs", {1: 1}),
            (HOST, "AnketaParser.Host/Source.cs", {1: 0}),
            (CLI, "AnketaParser.Cli/Source.cs", {}),
        ]
        moment = int(time.time())
        self.write(
            make_report(
                entries, sources=self.sources, line_rate=2 / 3, timestamp=moment
            )
        )
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )
        self.write(
            make_report(entries, sources=self.sources, line_rate=0.8, timestamp=moment)
        )
        errors = " ".join(validate(self.reports, "unit", self.repo, max_age_hours=24.0))
        self.assertIn("завышено", errors)


class SourceLineTests(DotnetCoverageTests):
    """Строки отчёта сверяются с фактическим исходником."""

    def test_relative_src_paths_without_sources_are_accepted(self):
        entries = [
            (assembly, f"src/{assembly}/Source.cs", {1: 1}) for assembly in ASSEMBLIES
        ]
        self.write(
            make_report(
                entries, sources=(), timestamp=int(time.time()), branches=(10, 6)
            )
        )
        self.assertEqual(
            validate(self.reports, "unit", self.repo, max_age_hours=24.0), []
        )

    def test_line_outside_source_is_rejected(self):
        entries = self.entries()
        entries[0] = (CORE, "AnketaParser.Core/Source.cs", {1: 1, 99: 1})
        self.write(
            make_report(
                entries,
                sources=self.sources,
                timestamp=int(time.time()),
                branches=(10, 6),
            )
        )
        errors = validate(self.reports, "unit", self.repo, max_age_hours=24.0)
        self.assertTrue(any("вне исходника" in error for error in errors), errors)

    def test_negative_hits_are_rejected(self):
        entries = self.entries()
        entries[1] = (INFRA, "AnketaParser.Infrastructure/Source.cs", {1: 1})
        report = make_report(
            entries, sources=self.sources, timestamp=int(time.time()), branches=(10, 6)
        )
        self.write(report.replace('hits="1"', 'hits="-1"', 1))
        errors = validate(self.reports, "unit", self.repo, max_age_hours=24.0)
        self.assertTrue(
            any("некорректные попадания" in error for error in errors), errors
        )


if __name__ == "__main__":
    _ = unittest.main()
