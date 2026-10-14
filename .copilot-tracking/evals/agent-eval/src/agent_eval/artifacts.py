# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Loaders that turn agent output folders and documents into analyzable objects."""

from __future__ import annotations

import fnmatch
import hashlib
import re
from dataclasses import dataclass, field
from functools import cached_property
from pathlib import Path
from typing import Any

from agent_eval.markdown import Chunk, definitions, ids_in, iter_chunks, leading_id, split_frontmatter

FR_SECTION = r"^functional requirements"
AC_SECTION = r"^acceptance criteria"
NFR_SECTION = r"^non-functional requirements"
BR_SECTION = r"^business rules"


def sha256_of(paths: list[Path]) -> str:
    """Stable content hash across one or more files."""
    digest = hashlib.sha256()
    for path in sorted(paths):
        digest.update(path.name.encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()[:16]


@dataclass
class DtBundle:
    """Design Thinking coaching outputs under ``.copilot-tracking/dt/<slug>/``."""

    root: Path
    profile: dict[str, Any]
    files: dict[str, str] = field(default_factory=dict)

    @classmethod
    def load(cls, root: Path, profile: dict[str, Any]) -> DtBundle:
        """Read every markdown file under ``root`` except excluded logs."""
        if not root.is_dir():
            raise FileNotFoundError(f"DT folder not found: {root}")
        excluded = {name.lower() for name in profile.get("exclude", [])}
        files = {
            path.relative_to(root).as_posix(): path.read_text(encoding="utf-8", errors="replace")
            for path in sorted(root.rglob("*.md"))
            if path.name.lower() not in excluded
        }
        return cls(root=root, profile=profile, files=files)

    def method_folders(self) -> dict[int, str]:
        """Return method number to folder name for method folders that exist."""
        found: dict[int, str] = {}
        for number, spec in self.profile["methods"].items():
            for child in sorted(p for p in self.root.iterdir() if p.is_dir()):
                if fnmatch.fnmatch(child.name.lower(), spec["folder"]):
                    found[int(number)] = child.name
                    break
        return found

    def find(self, stem: str, folder_glob: str | None = None) -> tuple[str, str] | None:
        """Return (relative path, text) of the first file whose stem contains ``stem``."""
        for rel, text in self.files.items():
            parts = rel.split("/")
            if folder_glob and (len(parts) < 2 or not fnmatch.fnmatch(parts[0].lower(), folder_glob)):
                continue
            if stem.lower() in Path(rel).stem.lower():
                return rel, text
        return None

    def files_in(self, folder_glob: str) -> dict[str, str]:
        """Return files whose top-level folder matches ``folder_glob``."""
        return {
            rel: text
            for rel, text in self.files.items()
            if "/" in rel and fnmatch.fnmatch(rel.split("/")[0].lower(), folder_glob)
        }

    def chunks(self, files: dict[str, str]) -> list[tuple[str, Chunk]]:
        """Chunk each file and pair chunks with their file path."""
        return [(rel, chunk) for rel, text in files.items() for chunk in iter_chunks(text)]

    def combined_text(self, max_chars: int | None = None) -> str:
        """Concatenate deliverable files for judge input."""
        state = self.profile.get("state_file", "")
        ordered = sorted(self.files, key=lambda r: (r != state, r))
        text = "\n\n".join(f"=== FILE: {rel} ===\n{self.files[rel]}" for rel in ordered)
        return text if max_chars is None else text[:max_chars]

    def fingerprint(self) -> str:
        """Content hash of the analyzed files."""
        return sha256_of([self.root / rel for rel in self.files])


@dataclass
class RequirementsDoc:
    """A BRD or PRD produced by the BRD Builder or PRD Builder agent."""

    kind: str
    path: Path
    text: str
    profile: dict[str, Any]

    @classmethod
    def load(cls, kind: str, path: Path, profile: dict[str, Any]) -> RequirementsDoc:
        """Read a requirements document from disk."""
        if not path.is_file():
            raise FileNotFoundError(f"{kind.upper()} file not found: {path}")
        return cls(kind=kind, path=path, text=path.read_text(encoding="utf-8", errors="replace"), profile=profile)

    @cached_property
    def frontmatter(self) -> dict[str, Any]:
        return split_frontmatter(self.text)[0]

    @cached_property
    def chunks(self) -> list[Chunk]:
        return iter_chunks(self.text)

    @property
    def goal_prefix(self) -> str:
        return self.profile["goal_prefix"]

    @cached_property
    def fr(self) -> dict[str, Chunk]:
        return definitions(self.chunks, "FR", FR_SECTION)

    @cached_property
    def ac(self) -> dict[str, Chunk]:
        return definitions(self.chunks, "AC", AC_SECTION)

    @cached_property
    def goals(self) -> dict[str, Chunk]:
        return definitions(self.chunks, self.goal_prefix, rf"^{self.profile['goal_section']}")

    @cached_property
    def nfr(self) -> dict[str, Chunk]:
        return definitions(self.chunks, "NFR", NFR_SECTION)

    @cached_property
    def br(self) -> dict[str, Chunk]:
        return definitions(self.chunks, "BR", BR_SECTION)

    def defined_ids(self, prefix: str) -> set[str]:
        """IDs with a defining chunk anywhere in the document."""
        lookup = {"FR": self.fr, "AC": self.ac, "NFR": self.nfr, "BR": self.br, self.goal_prefix: self.goals}
        if prefix in lookup:
            return set(lookup[prefix])
        return set(definitions(self.chunks, prefix))

    def links(self, source_id: str, target_prefix: str) -> set[str]:
        """IDs of ``target_prefix`` linked to ``source_id`` in either direction.

        Links come from the source's defining chunk, any traceability row that
        starts with the source ID, and any target definition that mentions it.
        """
        linked: set[str] = set()
        source_prefix = source_id.split("-")[0]
        for chunk in self.chunks:
            if leading_id(chunk, source_prefix) == source_id:
                linked.update(ids_in(chunk.text, (target_prefix,)))
        targets = definitions(self.chunks, target_prefix)
        for target_id, chunk in targets.items():
            if source_id in ids_in(chunk.text, (source_prefix,)):
                linked.add(target_id)
        return linked

    def priority(self, chunk: Chunk) -> str | None:
        """Return the normalized MoSCoW priority of a requirement chunk."""
        raw = chunk.cell("priority", "moscow")
        if raw is None:
            m = re.search(r"priority\s*[:|]\s*\**\s*(must|should|could|won'?t)", chunk.text, re.I)
            raw = m.group(1) if m else None
        if not raw:
            return None
        m = re.match(r"\s*\**\s*(must|should|could|won'?t)", raw, re.I)
        return m.group(1).lower().replace("'", "") if m else None

    def statement(self, chunk: Chunk) -> str:
        """Return the requirement statement text of a chunk."""
        return chunk.cell("requirement", "description", "statement", "title") or chunk.text

    def fingerprint(self) -> str:
        """Content hash of the document."""
        return sha256_of([self.path])
