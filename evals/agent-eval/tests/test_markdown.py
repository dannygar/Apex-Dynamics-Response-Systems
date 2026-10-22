# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Tests for the markdown chunker and ID helpers."""

from __future__ import annotations

from agent_eval.markdown import definitions, ids_in, iter_chunks, leading_id, split_frontmatter


def test_given_frontmatter_when_split_then_returns_mapping_and_body():
    # Arrange
    text = "---\ntitle: X\nversion: 0.1.0\n---\n# Heading\n"

    # Act
    meta, body = split_frontmatter(text)

    # Assert
    assert meta == {"title": "X", "version": "0.1.0"} and body.startswith("# Heading")


def test_given_table_when_chunked_then_rows_carry_header_cells_and_section():
    # Arrange
    text = "## Functional Requirements\n\n| ID | Requirement | Priority |\n|---|---|---|\n| FR-001 | Do X | Must |\n"

    # Act
    rows = [c for c in iter_chunks(text) if c.kind == "row"]

    # Assert
    assert rows[0].cell("priority") == "Must" and rows[0].section == "Functional Requirements"


def test_given_heading_inside_code_fence_when_chunked_then_not_treated_as_section():
    # Arrange
    text = "## Goals\n\n```text\n# not a heading\nBG-001: Goal\n```\n"

    # Act
    chunks = iter_chunks(text)

    # Assert
    assert all(c.section_path == ["Goals"] for c in chunks)


def test_given_bold_bullet_when_leading_id_then_returns_defined_id():
    # Arrange
    chunk = iter_chunks("* **AC-001**: Given x, When y, Then z.")[0]

    # Act
    result = leading_id(chunk, "AC")

    # Assert
    assert result == "AC-001"


def test_given_text_mentioning_id_midway_when_leading_id_then_returns_none():
    # Arrange
    chunk = iter_chunks("This paragraph mentions FR-001 in passing.")[0]

    # Act
    result = leading_id(chunk, "FR")

    # Assert
    assert result is None


def test_given_definitions_outside_section_when_scoped_then_only_section_ids_returned():
    # Arrange
    text = "## Functional Requirements\n\n* FR-001: A\n\n## Traceability Matrix\n\n* FR-002: B\n"

    # Act
    result = definitions(iter_chunks(text), "FR", r"^functional requirements")

    # Assert
    assert list(result) == ["FR-001"]


def test_given_mixed_ids_when_ids_in_with_prefixes_then_filters_and_dedupes():
    # Act
    result = ids_in("FR-001 AC-001 FR-001 BG-002", ("FR", "BG"))

    # Assert
    assert result == ["FR-001", "BG-002"]
