"""Focused tests for safe three-way PO/POT catalog merging."""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import polib

from merge_gettext import merge


class MergeGettextTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def catalog(self, name: str, entries: dict[str, str]) -> Path:
        path = self.root / f"{name}.po"
        po = polib.POFile(wrapwidth=78)
        po.metadata = {"Language": "en_US", "X-Generator": name}
        for msgid, msgstr in entries.items():
            po.append(polib.POEntry(msgid=msgid, msgstr=msgstr))
        po.save(str(path))
        return path

    def test_unions_new_entries_and_keeps_non_overlapping_translation_edits(self):
        base = self.catalog("base", {"shared": "base", "fork-only": ""})
        upstream = self.catalog("upstream", {"shared": "upstream", "upstream-only": "new"})
        fork = self.catalog("fork", {"shared": "base", "fork-only": "desktop"})
        output = self.root / "merged.po"

        merge(base, upstream, fork, output)

        result = polib.pofile(str(output))
        values = {entry.msgid: entry.msgstr for entry in result}
        self.assertEqual(values, {"shared": "upstream", "fork-only": "desktop", "upstream-only": "new"})

    def test_fork_translation_wins_only_when_both_sides_changed_same_translation(self):
        base = self.catalog("base", {"shared": "base"})
        upstream = self.catalog("upstream", {"shared": "upstream"})
        fork = self.catalog("fork", {"shared": "desktop"})
        output = self.root / "merged.po"

        merge(base, upstream, fork, output)

        self.assertEqual(polib.pofile(str(output))[0].msgstr, "desktop")

    def test_duplicate_entries_are_rejected(self):
        duplicate = self.root / "duplicate.po"
        duplicate.write_text('msgid "same"\nmsgstr "1"\n\nmsgid "same"\nmsgstr "2"\n', encoding="utf-8")
        empty = self.catalog("empty", {})

        with self.assertRaises(ValueError):
            merge(empty, duplicate, empty, self.root / "merged.po")


if __name__ == "__main__":
    unittest.main()
