# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Deterministic checks for Design Thinking coaching outputs."""

from __future__ import annotations

import re
from typing import Any

from agent_eval.artifacts import DtBundle
from agent_eval.config import compile_patterns, first_match
from agent_eval.markdown import Chunk, iter_chunks
from agent_eval.models import CheckResult

_HMW_RE = re.compile(r"how might we|^\s*\.\.\.|^\s*…", re.I)
_THEME_ID_RE = re.compile(r"^T?\d+$", re.I)
_HONESTY_RE = re.compile(
    r"tension|gap|under-?represented|single (primary )?(source|voice)|unvalidated|not validated|"
    r"assumption|unknown|conflicting|needs? validation",
    re.I,
)


def _check(name: str, title: str, score: float, **kwargs: Any) -> CheckResult:
    return CheckResult.scored(score, id=f"dt.{name}", layer="structure", title=title, **kwargs)


def requested_methods(bundle: DtBundle, methods: list[int] | None) -> list[int]:
    """Methods to evaluate: explicit request, else present folders (at least 1-3)."""
    if methods:
        return sorted(methods)
    present = set(bundle.method_folders())
    return sorted(present | {1, 2, 3})


def check_artifacts(bundle: DtBundle, methods: list[int]) -> CheckResult:
    spec = bundle.profile["methods"]
    folders = bundle.method_folders()
    expected = 0
    missing: list[str] = []
    for number in methods:
        files = spec.get(number, spec.get(str(number), {})).get("files", [])
        folder = spec.get(number, spec.get(str(number), {})).get("folder", f"method-0{number}-*")
        if number not in folders:
            expected += max(len(files), 1)
            missing.append(f"method {number}: folder {folder} missing")
            continue
        for stem in files:
            expected += 1
            if not bundle.find(stem, folder):
                missing.append(f"method {number}: {stem}.md missing")
    score = 1 - len(missing) / expected if expected else 1.0
    return _check(
        "artifacts", "Method artifacts present", score,
        detail=f"{expected - len(missing)}/{expected} expected artifacts for methods {methods}",
        evidence=missing[:10],
        guidance="Ask the DT Coach to write the missing method outputs before moving on.",
        data={"methods": methods},
    )


def check_state_file(bundle: DtBundle) -> CheckResult:
    present = bundle.profile["state_file"] in bundle.files
    return CheckResult.scored(
        1.0 if present else 0.0, id="dt.state_file", layer="structure", title="Coaching state recorded",
        weight=0.5, detail="coaching-state.md present" if present else "coaching-state.md missing",
        guidance="Start sessions with /dt-start-project so progress can be resumed.",
    )


def theme_rows(bundle: DtBundle) -> list[Chunk]:
    """Rows of the affinity table that define synthesis themes."""
    found = bundle.find(bundle.profile["themes_file"], bundle.profile["synthesis_folder"])
    if not found:
        return []
    rows = []
    for chunk in iter_chunks(found[1]):
        if chunk.kind != "row" or not chunk.cells:
            continue
        has_theme_col = any(h.strip().lower().startswith("theme") for h in chunk.header or [])
        if has_theme_col or _THEME_ID_RE.match(chunk.cells[0].strip("*` ")):
            rows.append(chunk)
    return rows


def check_theme_count(bundle: DtBundle) -> CheckResult:
    low, high = bundle.profile["theme_count_range"]
    count = len(theme_rows(bundle))
    score = 1.0 if low <= count <= high else (0.5 if count else 0.0)
    return _check(
        "theme_count", "Synthesis produced a usable set of themes", score,
        detail=f"{count} themes (expected {low}-{high})",
        guidance="Cluster findings into 5-15 themes; split catch-all clusters and merge near-duplicates.",
        data={"theme_count": count},
    )


def check_evidence(bundle: DtBundle, extra_patterns: list[str]) -> CheckResult:
    patterns = compile_patterns(bundle.profile["citation_patterns"] + extra_patterns)
    files = dict(found for stem in bundle.profile["evidence_files"] if (found := bundle.find(stem)))
    claims = [
        (rel, c) for rel, c in bundle.chunks(files)
        if c.kind in ("row", "bullet") and len(c.text) > 40 and not c.in_section(r"tension|not a theme|scope")
    ]
    if not claims:
        return _check("evidence", "Claims cite research evidence", 0.0, detail="No synthesis claims found")
    unsupported = [(rel, c) for rel, c in claims if not first_match(c.text, patterns)]
    return _check(
        "evidence", "Claims cite research evidence", 1 - len(unsupported) / len(claims),
        detail=f"{len(claims) - len(unsupported)}/{len(claims)} findings and themes cite a source or quote",
        evidence=[f"{rel}: {c.snippet(90)}" for rel, c in unsupported[:6]],
        guidance="Anchor each theme and insight in a quote or a named research source.",
    )


def hmw_items(bundle: DtBundle) -> list[str]:
    """How Might We questions from the HMW file."""
    found = bundle.find(bundle.profile["hmw_file"], bundle.profile["synthesis_folder"])
    if not found:
        return []
    items: list[str] = []
    for chunk in iter_chunks(found[1]):
        if chunk.kind == "row":
            cell = chunk.cell("how might we", "hmw", "question")
            if cell and _HMW_RE.search(cell):
                items.append(cell)
        elif chunk.kind == "bullet" and _HMW_RE.search(chunk.text):
            items.append(chunk.text)
    return items


def check_hmw(bundle: DtBundle) -> CheckResult:
    minimum = bundle.profile["min_hmw"]
    count = len(hmw_items(bundle))
    return _check(
        "hmw_count", "How Might We questions generated", min(1.0, count / minimum),
        detail=f"{count} HMW questions (minimum {minimum})",
        guidance="Turn each key insight into at least one open 'How might we…' question.",
        data={"hmw_count": count},
    )


def check_hmw_neutral(bundle: DtBundle, solution_terms: list[str]) -> CheckResult:
    items = hmw_items(bundle)
    if not items:
        return _check("hmw_neutral", "HMWs are solution-neutral", 0.0, detail="No HMW questions found")
    patterns = compile_patterns(solution_terms)
    leaks = [(item, hit) for item in items if (hit := first_match(item, patterns))]
    return _check(
        "hmw_neutral", "HMWs are solution-neutral", 1 - len(leaks) / len(items),
        detail=f"{len(items) - len(leaks)}/{len(items)} HMWs avoid naming a solution",
        evidence=[f"'{hit}' in: {' '.join(item.split())[:90]}" for item, hit in leaks[:6]],
        guidance="Rewrite HMWs that name a feature or UI element; frame the need, not the fix.",
    )


def check_research_honesty(bundle: DtBundle) -> CheckResult:
    files = {**bundle.files_in("method-01-*"), **bundle.files_in("method-02-*"),
             **bundle.files_in(bundle.profile["synthesis_folder"])}
    hits = sorted({m.group(0).lower() for text in files.values() for m in _HONESTY_RE.finditer(text)})
    score = min(1.0, len(hits) / 3)
    return _check(
        "research_honesty", "Gaps, tensions, and assumptions recorded", score, weight=0.5,
        detail=f"{len(hits)} distinct uncertainty markers ({', '.join(hits[:6])})",
        guidance="Record unvalidated assumptions, missing voices, and unresolved tensions explicitly.",
    )


def run_dt_checks(bundle: DtBundle, profiles: dict[str, Any], methods: list[int] | None,
                  extra_citations: list[str] | None = None) -> list[CheckResult]:
    """Run every structural check for a DT bundle.

    Args:
        bundle: Loaded DT outputs.
        profiles: Bundled profile configuration.
        methods: Method numbers to evaluate, or None to infer from folders.
        extra_citations: Case-specific citation regexes from the reference file.

    Returns:
        Structural check results in a stable order.
    """
    selected = requested_methods(bundle, methods)
    results = [check_artifacts(bundle, selected), check_state_file(bundle)]
    if 3 in selected:
        results += [
            check_theme_count(bundle),
            check_evidence(bundle, extra_citations or []),
            check_hmw(bundle),
            check_hmw_neutral(bundle, profiles["requirements"]["solution_terms"]),
        ]
    results.append(check_research_honesty(bundle))
    return results
