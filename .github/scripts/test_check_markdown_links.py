"""Синтетические регрессии навигации без сети и пользовательских данных.

Базовые сценарии адаптированы из проверки anki-deck-toolkit; расширены для
общего разбора с существующим skill checker и Windows-путей.
"""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import override

from check_markdown_links import check_page, check_repository


class MarkdownLinkTests(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temporary: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()

    @override
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name in ("docs/notes.md", "docs/a_(b).md", "docs/русский файл.md"):
            page = self.root / name
            page.parent.mkdir(parents=True, exist_ok=True)
            _ = page.write_text("# Страница\n", encoding="utf-8")

    def errors(self, text: str, name: str = "docs/page.md") -> list[str]:
        page = self.root / name
        page.parent.mkdir(parents=True, exist_ok=True)
        _ = page.write_text(text, encoding="utf-8")
        return check_page(self.root, page)

    def git(self, *args: str) -> None:
        # Синтетический локальный репозиторий, без shell и сетевых операций.
        _ = subprocess.run(  # noqa: S603
            ["git", "-C", str(self.root), *args],  # noqa: S607
            check=True,
            capture_output=True,
        )

    def test_valid_files_directories_images_and_encoded_paths(self) -> None:
        text = (
            '[рядом](notes.md "Заголовок") [скобки](a_(b).md)\n'
            "[каталог](../docs/) [корень](/docs/notes.md) ![изображение](notes.md)\n"
            "[угловая](<русский файл.md>) [кодирование](%6Eotes.md?plain=1#раздел)\n"
            "[перенос\nтекста](notes.md) [экранирование](a_\\(b\\).md)\n"
        )
        self.assertEqual(self.errors(text), [])

    def test_missing_target_reports_exact_file_line_and_address(self) -> None:
        self.assertEqual(
            self.errors("Текст\n\n[нет](missing.md#раздел)\n"),
            ["docs/page.md:3: цель ссылки не существует: missing.md#раздел"],
        )

    def test_reference_forms_and_multiline_definitions(self) -> None:
        self.assertEqual(
            self.errors("[текст][a] [a][] [a]\n\n[a]: notes.md\n[b]:\n  gone.md\n"),
            ["docs/page.md:5: цель ссылки не существует: gone.md"],
        )

    def test_footnotes_are_not_reference_destinations(self) -> None:
        for case, definition, reference_line in (
            ("single-line", "[^1]: Это пояснение.\n", 5),
            ("multiline", "[^1]: Это пояснение.\n    Продолжение сноски.\n", 6),
        ):
            with self.subTest(case=case):
                self.assertEqual(self.errors("Примечание[^1].\n\n" + definition), [])
                self.assertEqual(
                    self.errors(
                        "[нет](inline.md)\n\n"
                        + definition
                        + "\n[метка]: reference.md\n"
                    ),
                    [
                        "docs/page.md:1: цель ссылки не существует: inline.md",
                        f"docs/page.md:{reference_line}: "
                        + "цель ссылки не существует: reference.md",
                    ],
                )

    def test_missing_image_is_reported(self) -> None:
        self.assertEqual(
            self.errors("![нет](missing.png)"),
            ["docs/page.md:1: цель ссылки не существует: missing.png"],
        )

    def test_escape_outside_root_including_encoded_path(self) -> None:
        for case, address in enumerate(
            ("../../outside.md", "%2e%2e/%2e%2e/outside.md")
        ):
            with self.subTest(case=case):
                self.assertEqual(
                    self.errors(f"[наружу]({address})"),
                    [f"docs/page.md:1: ссылка ведёт за пределы репозитория: {address}"],
                )

    def test_external_urls_and_anchors_are_ignored(self) -> None:
        self.assertEqual(
            self.errors(
                "[сайт](https://example.com/x) [почта](mailto:a@b.c)\n"
                + "[ftp](ftp://example.com/f) [сеть](//example.com/a) [якорь](#раздел)\n"
                + "<https://example.com/[x](missing.md)>"
            ),
            [],
        )

    def test_code_comments_and_escaped_brackets_are_not_links(self) -> None:
        self.assertEqual(
            self.errors(
                "Пример `[x](absent.md)` и `` ` [y](absent.md) ``.\n\n"
                + "```markdown\n[x](absent.md)\n```\n\n"
                + "~~~~\n[x](absent.md)\n~~~~\n\n"
                + "<!-- [x](absent.md)\n[y](absent.md) -->\n"
                + "\\[пример](absent.md)\n\n"
                + "- ```markdown\n  [x](absent.md)\n  ```\n"
            ),
            [],
        )

    def test_multiline_span_preserves_following_line_numbers(self) -> None:
        self.assertEqual(
            self.errors("`код\n[x](absent.md)\n`\n[нет](gone.md)\n"),
            ["docs/page.md:4: цель ссылки не существует: gone.md"],
        )

    def test_backtick_escape_parity_preserves_active_links(self) -> None:
        for count in range(5):
            with self.subTest(backslashes=count):
                slashes = "\\" * count
                source = f"{slashes}`[пример](missing.md){slashes}`\n[нет](gone.md)\n"
                expected = ["docs/page.md:2: цель ссылки не существует: gone.md"]
                if count % 2:
                    expected.insert(
                        0, "docs/page.md:1: цель ссылки не существует: missing.md"
                    )
                self.assertEqual(self.errors(source), expected)

    def test_escaped_backtick_does_not_consume_following_real_span(self) -> None:
        self.assertEqual(
            self.errors("\\`[нет](missing.md) `[пример](absent.md)`\n[нет](gone.md)\n"),
            [
                "docs/page.md:1: цель ссылки не существует: missing.md",
                "docs/page.md:2: цель ссылки не существует: gone.md",
            ],
        )

    def test_backslash_inside_code_does_not_escape_closing_backtick(self) -> None:
        self.assertEqual(
            self.errors("`[пример](absent.md)\\` [нет](missing.md)\n"),
            ["docs/page.md:1: цель ссылки не существует: missing.md"],
        )

    def test_escaped_first_backtick_leaves_remaining_run_as_delimiter(self) -> None:
        self.assertEqual(
            self.errors("\\``[пример](absent.md)` [нет](missing.md)\n"),
            ["docs/page.md:1: цель ссылки не существует: missing.md"],
        )

    def test_blank_line_and_unmatched_backtick_do_not_hide_links(self) -> None:
        self.assertEqual(
            self.errors("Один `символ\n\n[нет](gone.md)\n"),
            ["docs/page.md:3: цель ссылки не существует: gone.md"],
        )

    def test_code_span_does_not_cross_block_boundary(self) -> None:
        self.assertEqual(
            self.errors(
                "# Первый заголовок `\n[нет](missing.md)\n# Второй заголовок `\n"
            ),
            ["docs/page.md:2: цель ссылки не существует: missing.md"],
        )
        self.assertEqual(self.errors("# Заголовок `[нет](absent.md)`\n"), [])

    def test_comment_content_is_not_parsed_as_code_span(self) -> None:
        self.assertEqual(
            self.errors("<!-- ` -->\n[нет](missing.md)\n`пример`\n"),
            ["docs/page.md:2: цель ссылки не существует: missing.md"],
        )
        self.assertEqual(
            self.errors("`<!--`\n[нет](missing.md)\n"),
            ["docs/page.md:2: цель ссылки не существует: missing.md"],
        )

    def test_reference_definition_does_not_interrupt_paragraph(self) -> None:
        self.assertEqual(self.errors("Описание формата:\n[ключ]: missing.md\n"), [])
        self.assertEqual(
            self.errors("Описание формата:\n\n[ключ]: missing.md\n"),
            ["docs/page.md:3: цель ссылки не существует: missing.md"],
        )

    def test_reference_definitions_inside_containers(self) -> None:
        for case, source, line in (
            ("quote", "> [bad]: missing.md\n\n[bad]\n", 1),
            ("list", "- [bad]: missing.md\n", 1),
            ("quoted-run", "> [ok]: notes.md\n> [bad]: missing.md\n", 2),
        ):
            with self.subTest(case=case):
                self.assertEqual(
                    self.errors(source),
                    [f"docs/page.md:{line}: цель ссылки не существует: missing.md"],
                )

    def test_title_does_not_end_definition_run(self) -> None:
        self.assertEqual(
            self.errors('[ok]: notes.md "Главная"\n[bad]: missing.md\n\n[bad]\n'),
            ["docs/page.md:2: цель ссылки не существует: missing.md"],
        )

    def test_comment_is_hidden_wherever_it_starts_in_line(self) -> None:
        for case, source in (
            ("text-prefix", "Текст <!-- [x](absent.md) -->\n"),
            ("space-prefix", "  <!-- [x](absent.md) -->\n"),
            ("after-code", "`код` <!-- [x](absent.md) -->\n"),
        ):
            with self.subTest(case=case):
                self.assertEqual(self.errors(source), [])

    def test_indented_examples_and_list_or_paragraph_continuations(self) -> None:
        self.assertEqual(
            self.errors(
                "Пример:\n\n    [x](absent.md)\n\n    [y](absent.md)\n"
                + "Текст [нет](gone.md).\n\n1. Пункт.\n\n    [нет](lost.md)\n\n"
                + "Абзац\n    [нет](missing.md)\n"
            ),
            [
                "docs/page.md:6: цель ссылки не существует: gone.md",
                "docs/page.md:10: цель ссылки не существует: lost.md",
                "docs/page.md:13: цель ссылки не существует: missing.md",
            ],
        )

    def test_unclosed_list_fence_does_not_hide_following_document(self) -> None:
        self.assertEqual(
            self.errors("- ```md\n  [x](absent.md)\n\n[нет](gone.md)\n"),
            ["docs/page.md:4: цель ссылки не существует: gone.md"],
        )

    def test_list_relative_indented_code_and_adjacent_links(self) -> None:
        for case, marker in (
            ("dash", "- "),
            ("plus", "+ "),
            ("star", "* "),
            ("ordered", "1. "),
            ("wide-ordered", "12) "),
            ("indented-marker", "  - "),
        ):
            with self.subTest(case=case):
                indent = " " * len(marker)
                self.assertEqual(
                    self.errors(
                        f"{marker}Пункт\n\n{indent}    [пример](absent.md)\n\n"
                        + f"{indent}    [пример](also-absent.md)\n\n"
                        + f"{indent}[продолжение](missing.md)\n\n[абзац](gone.md)\n"
                    ),
                    [
                        "docs/page.md:7: цель ссылки не существует: missing.md",
                        "docs/page.md:9: цель ссылки не существует: gone.md",
                    ],
                )

    def test_nested_list_code_dedents_to_parent_and_document(self) -> None:
        self.assertEqual(
            self.errors(
                "- Внешний пункт\n  1. Вложенный\n\n"
                + "         [пример](absent.md)\n"
                + "     [продолжение](nested.md)\n\n"
                + "  [родитель](parent.md)\n\n[абзац](gone.md)\n"
            ),
            [
                "docs/page.md:5: цель ссылки не существует: nested.md",
                "docs/page.md:7: цель ссылки не существует: parent.md",
                "docs/page.md:9: цель ссылки не существует: gone.md",
            ],
        )

    def test_list_indentation_does_not_hide_paragraph_or_following_code(self) -> None:
        self.assertEqual(
            self.errors(
                "- Пункт\n      [продолжение](missing.md)\n\n"
                + "Абзац\n    [продолжение](gone.md)\n\n"
                + "    [пример](absent.md)\n\n[ссылка](last.md)\n"
            ),
            [
                "docs/page.md:2: цель ссылки не существует: missing.md",
                "docs/page.md:5: цель ссылки не существует: gone.md",
                "docs/page.md:9: цель ссылки не существует: last.md",
            ],
        )

    def test_lazy_list_continuation_preserves_container_for_later_code(self) -> None:
        self.assertEqual(
            self.errors(
                "- Пункт\n[продолжение](missing.md)\n\n"
                + "      [пример](absent.md)\n\n[абзац](gone.md)\n"
            ),
            [
                "docs/page.md:2: цель ссылки не существует: missing.md",
                "docs/page.md:6: цель ссылки не существует: gone.md",
            ],
        )
        self.assertEqual(
            self.errors(
                "123. Пункт\nПродолжение\n\n     [ссылка](missing.md)\n\n"
                + "         [пример](absent.md)\n\n[абзац](gone.md)\n"
            ),
            [
                "docs/page.md:4: цель ссылки не существует: missing.md",
                "docs/page.md:8: цель ссылки не существует: gone.md",
            ],
        )

    def test_code_at_start_of_list_item_and_nested_fences(self) -> None:
        for case, source, last_line in (
            ("initial-code", "-     [пример](absent.md)\n\n", 3),
            (
                "nested-closed",
                "- Пункт\n  1. ```md\n     [пример](absent.md)\n     ```\n\n",
                6,
            ),
            ("nested-unclosed", "- Пункт\n  1. ```md\n     [пример](absent.md)\n\n", 5),
        ):
            with self.subTest(case=case):
                self.assertEqual(
                    self.errors(source + "[абзац](gone.md)\n"),
                    [f"docs/page.md:{last_line}: цель ссылки не существует: gone.md"],
                )

    def test_even_backslashes_leave_real_link_visible(self) -> None:
        self.assertEqual(
            self.errors("\\\\[нет](gone.md)"),
            ["docs/page.md:1: цель ссылки не существует: gone.md"],
        )

    def test_repository_reads_tracked_unicode_markdown_only(self) -> None:
        self.git("init", "-q")
        _ = (self.root / "docs/русский файл.md").write_text(
            "[нет](gone.md)", encoding="utf-8"
        )
        _ = (self.root / "untracked.md").write_text("[нет](gone.md)", encoding="utf-8")
        self.git("add", "--", "docs")
        self.assertEqual(
            check_repository(self.root),
            ["docs/русский файл.md:1: цель ссылки не существует: gone.md"],
        )

    def test_missing_tracked_document_is_not_silently_skipped(self) -> None:
        self.git("init", "-q")
        self.git("add", "--", "docs")
        (self.root / "docs/notes.md").unlink()
        errors = check_repository(self.root)
        self.assertEqual(len(errors), 1, errors)
        self.assertIn("docs/notes.md: не удалось прочитать Markdown", errors[0])

    def test_cli_success_and_failure_exit_codes(self) -> None:
        self.git("init", "-q")
        self.git("add", "--", "docs")
        checker = Path(__file__).with_name("check_markdown_links.py")
        for case, source, code in (
            ("valid", "[да](notes.md)", 0),
            ("missing", "[нет](gone.md)", 1),
        ):
            with self.subTest(case=case):
                _ = (self.root / "docs/page.md").write_text(source, encoding="utf-8")
                self.git("add", "--", "docs/page.md")
                # Ненулевой результат — проверяемый отказ, поэтому check=False.
                result = subprocess.run(  # noqa: S603
                    [sys.executable, str(checker), "--root", str(self.root)],
                    capture_output=True,
                    check=False,
                    env=dict(os.environ, PYTHONIOENCODING="utf-8"),
                )
                self.assertEqual(result.returncode, code, result.stderr)
                if code:
                    self.assertIn(b"docs/page.md:1", result.stderr)
                else:
                    self.assertIn("Локальные ссылки".encode(), result.stdout)


if __name__ == "__main__":
    _ = unittest.main()
