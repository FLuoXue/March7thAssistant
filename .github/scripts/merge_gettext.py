"""Merge a three-way gettext conflict by entry key before source extraction."""
from __future__ import annotations

import copy
import sys
from pathlib import Path

import polib


def key(entry: polib.POEntry) -> tuple[str, str]:
    return entry.msgctxt or "", entry.msgid


def field(entry: polib.POEntry | None, name: str):
    if entry is None:
        return None
    value = getattr(entry, name)
    if name == "msgstr_plural":
        return dict(value)
    return value


def merge_field(base, upstream, fork):
    """Keep independent edits; fork wins only when both sides changed one field."""
    if upstream == base:
        return fork
    if fork == base or upstream == fork:
        return upstream
    return fork


def merge(base_path: Path, upstream_path: Path, fork_path: Path, output_path: Path) -> None:
    base = polib.pofile(str(base_path), check_for_duplicates=True)
    upstream = polib.pofile(str(upstream_path), check_for_duplicates=True)
    fork = polib.pofile(str(fork_path), check_for_duplicates=True)
    base_entries = {key(entry): entry for entry in base}
    upstream_entries = {key(entry): entry for entry in upstream}
    fork_entries = {key(entry): entry for entry in fork}

    result = polib.POFile(wrapwidth=78)
    result.metadata = dict(upstream.metadata)
    for name, value in fork.metadata.items():
        if name not in base.metadata or fork.metadata[name] != base.metadata.get(name):
            if name not in upstream.metadata or upstream.metadata[name] == base.metadata.get(name):
                result.metadata[name] = value

    ordered_keys = list(dict.fromkeys([*upstream_entries, *fork_entries]))
    for entry_key in ordered_keys:
        base_entry = base_entries.get(entry_key)
        upstream_entry = upstream_entries.get(entry_key)
        fork_entry = fork_entries.get(entry_key)
        if upstream_entry is None:
            selected = fork_entry
        elif fork_entry is None:
            selected = upstream_entry
        else:
            selected = copy.deepcopy(upstream_entry)
            for name in ("msgid_plural", "msgstr", "msgstr_plural"):
                value = merge_field(field(base_entry, name), field(upstream_entry, name), field(fork_entry, name))
                if name == "msgstr_plural":
                    value = value or {}
                setattr(selected, name, value)
        if selected is not None:
            result.append(copy.deepcopy(selected))

    output_path.parent.mkdir(parents=True, exist_ok=True)
    result.save(str(output_path))


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: merge_gettext.py BASE UPSTREAM FORK OUTPUT")
    merge(*(Path(part) for part in sys.argv[1:]))
