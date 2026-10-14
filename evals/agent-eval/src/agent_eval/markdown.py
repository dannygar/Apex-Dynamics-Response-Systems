# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Lightweight markdown parsing tuned for HVE Core planning artifacts.

The parser splits a document into *chunks*: table rows, bullet items, and
paragraphs. Each chunk remembers the heading path it sits under and, for
table rows, its header and cells. Checks operate on chunks so they work the
same whether an agent wrote requirements as tables, bullets, or code-fenced
blocks.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Literal

import yaml

ID_RE = re.compile(r"\b([A-Z]{1,5})-(\d{3,})\b")
_HEADING_RE = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
_BULLET_RE = re.compile(r"^\s*(?:[*+-]|\d+[.)])\s+")
_TABLE_SEP_RE = re.compile(r"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")
_FENCE_RE = re.compile(r"^\s*(```|~~~)")
_FRONTMATTER_RE = re.compile(r"\A---\s*\r?\n(.*?)\r?\n---\s*(?:\r?\n|\Z)", re.S)

ChunkKind = Literal["paragraph", "bullet", "row"]


@dataclass
class Chunk:
    """A unit of document content with its structural context."""

    text: str
    kind: ChunkKind
    section_path: list[str] = field(default_factory=list)
    line: int = 0
    header: list[str] | None = None
    cells: list[str] | None = None

    @property
    def section(self) -> str:
        """Heading path joined for display."""
        return " > ".join(self.section_path)

    def in_section(self, pattern: str) -> bool:
        """Return True when any heading in the path matches ``pattern`` (case-insensitive)."""
        rx = re.compile(pattern, re.I)
        return any(rx.search(title) for title in self.section_path)

    def cell(self, *names: str) -> str | None:
        """Return the cell under the first header whose text starts with one of ``names``."""
        if not self.header or not self.cells:
            return None
        for name in names:
            for index, head in enumerate(self.header):
                if head.strip().lower().startswith(name.lower()) and index < len(self.cells):
                    return self.cells[index]
        return None

    def snippet(self, limit: int = 160) -> str:
        """Single-line preview of the chunk."""
        flat = " ".join(self.text.split())
        return flat if len(flat) <= limit else flat[: limit - 1] + "…"


def split_frontmatter(text: str) -> tuple[dict, str]:
    """Split YAML frontmatter from the body.

    Args:
        text: Full markdown document.

    Returns:
        Tuple of (frontmatter mapping, body). The mapping is empty when the
        document has no frontmatter or it cannot be parsed.
    """
    match = _FRONTMATTER_RE.match(text)
    if not match:
        return {}, text
    try:
        data = yaml.safe_load(match.group(1)) or {}
    except yaml.YAMLError:
        data = {}
    return (data if isinstance(data, dict) else {}), text[match.end() :]


def _split_row(line: str) -> list[str]:
    stripped = line.strip()
    if stripped.startswith("|"):
        stripped = stripped[1:]
    if stripped.endswith("|") and not stripped.endswith("\\|"):
        stripped = stripped[:-1]
    cells = re.split(r"(?<!\\)\|", stripped)
    return [c.strip().replace("\\|", "|") for c in cells]


def headings(text: str) -> list[tuple[int, str]]:
    """Return (level, title) for every heading outside code fences."""
    result: list[tuple[int, str]] = []
    in_fence = False
    for line in text.splitlines():
        if _FENCE_RE.match(line):
            in_fence = not in_fence
            continue
        if not in_fence and (m := _HEADING_RE.match(line)):
            result.append((len(m.group(1)), m.group(2).strip()))
    return result


def iter_chunks(text: str) -> list[Chunk]:
    """Split markdown into chunks with section context.

    Args:
        text: Markdown body (frontmatter may be included; it is skipped).

    Returns:
        Ordered list of chunks.
    """
    _, body = split_frontmatter(text)
    offset = text.count("\n") - body.count("\n")
    lines = body.splitlines()
    chunks: list[Chunk] = []
    stack: list[tuple[int, str]] = []
    in_fence = False
    current: list[str] = []
    current_kind: ChunkKind | None = None
    current_line = 0
    table_header: list[str] | None = None

    def path() -> list[str]:
        return [title for _, title in stack]

    def flush() -> None:
        nonlocal current, current_kind
        if current and current_kind:
            joined = "\n".join(current).strip()
            if joined:
                chunks.append(Chunk(joined, current_kind, path(), current_line))
        current, current_kind = [], None

    for index, line in enumerate(lines, start=1 + offset):
        if _FENCE_RE.match(line):
            in_fence = not in_fence
            flush()
            table_header = None
            continue
        if not in_fence and (m := _HEADING_RE.match(line)):
            flush()
            table_header = None
            level = len(m.group(1))
            while stack and stack[-1][0] >= level:
                stack.pop()
            stack.append((level, m.group(2).strip()))
            continue
        stripped = line.strip()
        if not in_fence and stripped.startswith("|"):
            flush()
            if _TABLE_SEP_RE.match(stripped):
                continue
            cells = _split_row(stripped)
            if table_header is None:
                table_header = cells
                continue
            if all(not c for c in cells):
                continue
            chunks.append(Chunk(stripped, "row", path(), index, header=table_header, cells=cells))
            continue
        table_header = None
        if not stripped:
            flush()
            continue
        if not in_fence and _BULLET_RE.match(line):
            flush()
            current, current_kind, current_line = [line], "bullet", index
            continue
        if current_kind is None:
            current, current_kind, current_line = [line], "paragraph", index
        else:
            current.append(line)
    flush()
    return chunks


def chunks_in(chunks: list[Chunk], section_pattern: str) -> list[Chunk]:
    """Filter chunks to those under a heading matching ``section_pattern``."""
    return [c for c in chunks if c.in_section(section_pattern)]


def ids_in(text: str, prefixes: tuple[str, ...] | None = None) -> list[str]:
    """Return unique IDs like ``FR-001`` in order of appearance."""
    seen: dict[str, None] = {}
    for m in ID_RE.finditer(text):
        if prefixes is None or m.group(1) in prefixes:
            seen.setdefault(f"{m.group(1)}-{m.group(2)}", None)
    return list(seen)


_LEADING_RE = re.compile(r"^\s*(?:[*+-]|\d+[.)])?\s*(?:\*\*|__)?`?([A-Z]{1,5}-\d{3,})`?(?:\*\*|__)?\s*(?:[:|.—–-]|$)")


def leading_id(chunk: Chunk, prefix: str) -> str | None:
    """Return the ID a chunk *defines*, if it starts with ``prefix``-###.

    Table rows define the ID in their first cell; bullets and paragraphs
    define it when the text starts with the ID (optionally bolded).
    """
    if chunk.kind == "row" and chunk.cells:
        first = chunk.cells[0].strip("*`_ ")
        return first if re.fullmatch(rf"{re.escape(prefix)}-\d{{3,}}", first) else None
    m = _LEADING_RE.match(chunk.text)
    if m and m.group(1).split("-")[0] == prefix:
        return m.group(1)
    return None


def definitions(chunks: list[Chunk], prefix: str, section_pattern: str | None = None) -> dict[str, Chunk]:
    """Map each ``prefix`` ID to the first chunk that defines it.

    Args:
        chunks: Document chunks.
        prefix: ID prefix such as ``FR`` or ``BG``.
        section_pattern: Optional heading regex restricting where definitions
            are searched. Falls back to the whole document when nothing in the
            section defines an ID.

    Returns:
        Ordered mapping of ID to defining chunk.
    """
    pool = chunks_in(chunks, section_pattern) if section_pattern else chunks
    found: dict[str, Chunk] = {}
    for chunk in pool:
        if (ident := leading_id(chunk, prefix)) and ident not in found:
            found[ident] = chunk
    if not found and section_pattern:
        return definitions(chunks, prefix, None)
    return found


def duplicate_definitions(chunks: list[Chunk], prefix: str, section_pattern: str) -> list[str]:
    """Return IDs defined more than once inside the same section scope."""
    counts: dict[str, int] = {}
    for chunk in chunks_in(chunks, section_pattern):
        if ident := leading_id(chunk, prefix):
            counts[ident] = counts.get(ident, 0) + 1
    return [i for i, n in counts.items() if n > 1]
