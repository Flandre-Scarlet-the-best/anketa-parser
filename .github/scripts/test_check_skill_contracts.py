"""Синтетические примеры для проверки метаданных Agent Skills."""

import tempfile
import unittest
from pathlib import Path

from check_skill_contracts import validate_skill, validate_tree


class SkillContractTests(unittest.TestCase):
    def test_plain_scalar_punctuation_is_valid(self):
        for ending in ("]", "}", "[", "{", ",", "\\"):
            with self.subTest(ending=ending):
                self.write_skill(f"---\nname: sample-skill\ndescription: Process arrays ending in {ending}\n---\n")
                self.assertEqual(validate_skill(self.file), [])

    def test_indented_yaml_is_parsed(self):
        self.write_skill("---\nname: sample-skill\ndescription: Demo.\nmetadata:\n  author: [broken\n---\n")
        self.assertTrue(validate_skill(self.file))
        self.write_skill("---\nname: sample-skill\ndescription: Demo.\nmetadata:\n  author: sample\n---\n")
        self.assertEqual(validate_skill(self.file), [])

    def test_frontmatter_types_and_decoded_description_length(self):
        for fields in ("description: []", "description: 123", "description: true", "description: {a: b}",
                       "description: Demo.\nmetadata: {author: [a, b]}",
                       "description: Demo.\nmetadata: []", "description: Demo.\nmetadata: {author: 123}"):
            with self.subTest(fields=fields):
                self.write_skill(f"---\nname: sample-skill\n{fields}\n---\n")
                self.assertTrue(validate_skill(self.file))
        self.write_skill('---\nname: sample-skill\ndescription: "' + r'\u0061' * 1024 + '"\n---\n')
        self.assertEqual(validate_skill(self.file), [])
        self.write_skill('---\nname: "sample-skill"\ndescription: "' + r'\u0061' * 1025 + '"\n---\n')
        self.assertTrue(any("1024" in error for error in validate_skill(self.file)))

    def test_unsafe_yaml_tags_are_rejected(self):
        self.write_skill('---\nname: sample-skill\ndescription: !!python/object/apply:builtins.str [unsafe]\n---\n')
        self.assertTrue(validate_skill(self.file))

    def test_duplicate_yaml_keys_are_rejected(self):
        for fields in ("description: One.\ndescription: Two.", "description: Demo.\nmetadata: {author: a, author: b}"):
            with self.subTest(fields=fields):
                self.write_skill(f"---\nname: sample-skill\n{fields}\n---\n")
                self.assertTrue(validate_skill(self.file))

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.folder = self.root / ".agents/skills/sample-skill"
        self.folder.mkdir(parents=True)
        self.file = self.folder / "SKILL.md"

    def write_skill(self, metadata=None, body="See [details](references/details.md)."):
        self.file.write_text(metadata or "---\nname: sample-skill\ndescription: >-\n  Use this to perform a concrete skill task.\n---\n" + body, encoding="utf-8")

    def test_valid_skill_and_local_ref(self):
        (self.folder / "references").mkdir()
        (self.folder / "references/details.md").write_text("Details", encoding="utf-8")
        self.write_skill()
        self.assertEqual(validate_tree(self.root), [])

    def test_invalid_name_and_nonstandard_routing_key(self):
        self.write_skill("---\nname: wrong-name\ndescription: Demo skill.\nwhenToUse: never\n---\n")
        self.assertTrue(any("name" in x for x in validate_skill(self.file)))
        self.assertTrue(any("whenToUse" in x for x in validate_skill(self.file)))

    def test_missing_reference(self):
        self.write_skill()
        self.assertTrue(any("ссылка" in x for x in validate_skill(self.file)))

    def test_nested_reference_links_are_checked(self):
        (self.folder / "references").mkdir()
        reference = self.folder / "references/details.md"
        self.write_skill()
        reference.write_text("[Вложенная ссылка](absent.md)", encoding="utf-8")
        self.assertTrue(any("absent.md" in error for error in validate_tree(self.root)))
        reference.write_text("[Вход](../SKILL.md)", encoding="utf-8")
        self.assertEqual(validate_tree(self.root), [])

    def test_escape_and_root_map_links_are_checked(self):
        self.write_skill(body="[Выход](../../outside.md)")
        (self.root / ".agents/outside.md").write_text("Текст", encoding="utf-8")
        self.assertTrue(any("ссылка" in error for error in validate_tree(self.root)))
        self.write_skill(body="Без ссылок.")
        (self.root / "AGENTS.md").write_text("[Карта](missing.md)", encoding="utf-8")
        self.assertTrue(any("missing.md" in error for error in validate_tree(self.root)))

    def test_description_limit(self):
        self.write_skill("---\nname: sample-skill\ndescription: >-\n  " + "x" * 1025 + "\n---\n")
        self.assertTrue(any("1024" in x for x in validate_skill(self.file)))

    def test_missing_frontmatter(self):
        self.write_skill("# Just text")
        self.assertTrue(any("frontmatter" in x for x in validate_skill(self.file)))

    def test_directory_without_manifest_fails(self):
        self.write_skill()
        (self.folder / "SKILL.md").unlink()
        self.assertTrue(any("SKILL.md" in x for x in validate_tree(self.root)))

    def test_broken_yaml_values_are_rejected(self):
        for value in ("[broken", '"unterminated', "'unterminated", "{a: 1", '"первая" "вторая"'):
            with self.subTest(value=value):
                self.write_skill(f"---\nname: sample-skill\ndescription: {value}\n---\n")
                self.assertTrue(any("неразбираемое" in x for x in validate_skill(self.file)))

    def test_empty_block_scalars_are_rejected(self):
        for marker in ("|", "|-", "|+", ">", ">-", ">+"):
            with self.subTest(marker=marker):
                self.write_skill(f"---\nname: sample-skill\ndescription: {marker}\n---\n")
                self.assertTrue(any("1024" in x for x in validate_skill(self.file)), marker)

    def test_filled_block_scalar_is_accepted(self):
        for marker in ("|", "|+", ">-"):
            with self.subTest(marker=marker):
                self.write_skill(f"---\nname: sample-skill\ndescription: {marker}\n  Достаточное описание.\n---\n", body="Без ссылок.")
                self.assertEqual(validate_skill(self.file), [])

    def test_flow_collections_are_accepted(self):
        # `metadata` — YAML-отображение, поэтому корректные flow-структуры допустимы.
        for value in ("{author: x}", "{author: 'a, b'}", "{author: '}'}"):
            with self.subTest(value=value):
                self.write_skill(f"---\nname: sample-skill\ndescription: Demo.\nmetadata: {value}\n---\n", body="Без ссылок.")
                self.assertEqual(validate_skill(self.file), [])

    def test_unbalanced_flow_collections_are_rejected(self):
        for value in ("{author: x", "[broken", "{author: x} extra", "}"):
            with self.subTest(value=value):
                self.write_skill(f"---\nname: sample-skill\ndescription: Demo.\nmetadata: {value}\n---\n", body="Без ссылок.")
                self.assertTrue(validate_skill(self.file), value)

    def test_valid_quoted_and_block_scalars(self):
        self.write_skill('---\nname: sample-skill\ndescription: "Краткое описание."\n---\n', body="Без ссылок.")
        self.assertEqual(validate_skill(self.file), [])
        self.write_skill("---\nname: sample-skill\ndescription: 'It''s fine.'\n---\n", body="Без ссылок.")
        self.assertEqual(validate_skill(self.file), [])
        self.write_skill(body="Без ссылок.")
        self.assertEqual(validate_skill(self.file), [])


if __name__ == "__main__":
    unittest.main()
