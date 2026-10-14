# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Reference-coverage layer: compare outputs with a case-specific expectation file."""

from __future__ import annotations

import re
from typing import Any

from agent_eval.artifacts import DtBundle, RequirementsDoc
from agent_eval.checks_dt import theme_rows
from agent_eval.config import compile_patterns
from agent_eval.markdown import Chunk
from agent_eval.models import CheckResult

_WONT_SECTION_RE = re.compile(r"won'?t|out of scope|non-goals?|exclu|not in scope", re.I)


def _all_of(chunk_text: str, patterns: list[re.Pattern[str]]) -> bool:
    return all(p.search(chunk_text) for p in patterns)


def _items_check(check_id: str, title: str, items: list[dict[str, Any]], guidance: str,
                 weight: float = 1.0) -> CheckResult:
    total = sum(i.get("weight", 1.0) for i in items) or 1.0
    earned = sum(i["score"] * i.get("weight", 1.0) for i in items)
    hits = sum(1 for i in items if i["score"] >= 1.0)
    evidence = [
        f"{'✔' if i['score'] >= 1 else ('◐' if i['score'] > 0 else '✘')} {i['label']}"
        + (f" — {i['evidence']}" if i.get("evidence") else "")
        for i in items
    ]
    return CheckResult.scored(
        earned / total, id=check_id, layer="reference", title=title, weight=weight,
        detail=f"{hits}/{len(items)} fully matched", evidence=evidence, guidance=guidance,
        data={"items": [{k: v for k, v in i.items() if k != "weight"} for i in items]},
    )


def _match_any(chunks: list[tuple[str, Chunk]], spec: dict[str, Any]) -> tuple[str, Chunk] | None:
    patterns = compile_patterns(spec["all_of"])
    for rel, chunk in chunks:
        if _all_of(chunk.text, patterns):
            return rel, chunk
    return None


def dt_reference_checks(bundle: DtBundle, ref: dict[str, Any]) -> list[CheckResult]:
    """Check DT outputs against expected themes, stakeholders, and non-themes."""
    spec = ref.get("dt") or {}
    results: list[CheckResult] = []
    scope_files: dict[str, str] = {}
    for folder in spec.get("scope", ["method-03-*"]):
        scope_files.update(bundle.files_in(folder))
    chunks = bundle.chunks(scope_files)

    if themes := spec.get("themes"):
        items = []
        for theme in themes:
            hit = _match_any(chunks, theme)
            items.append({
                "id": theme["id"], "label": theme["label"], "weight": theme.get("weight", 1.0),
                "score": 1.0 if hit else 0.0,
                "evidence": f"{hit[0]}: {hit[1].snippet(80)}" if hit else "",
            })
        results.append(_items_check(
            "ref.dt.themes", "Expected insights surfaced", items,
            "Revisit the research for the missing insight; check whether synthesis merged it away.", 2.0,
        ))

    if stakeholders := spec.get("stakeholders"):
        found = bundle.find(stakeholders.get("file", "stakeholder-map"))
        text = found[1] if found else ""
        items = [
            {"id": name, "label": name, "score": 1.0 if re.search(rf"\b{re.escape(name)}\b", text, re.I) else 0.0}
            for name in stakeholders["names"]
        ]
        results.append(_items_check(
            "ref.dt.stakeholders", "All research participants mapped", items,
            "Add every interviewed stakeholder to the stakeholder map, including low-power voices.",
        ))

    if not_themes := spec.get("not_themes"):
        rows = theme_rows(bundle)
        items = []
        for item in not_themes:
            pattern = re.compile(item["pattern"], re.I)
            offender = next((r for r in rows if pattern.search(r.text)), None)
            items.append({
                "id": item["id"], "label": item["label"], "score": 0.0 if offender else 1.0,
                "evidence": f"appears as theme: {offender.snippet(70)}" if offender else "",
            })
        results.append(_items_check(
            "ref.dt.not_themes", "No unsupported themes", items,
            "Themes need user evidence; move sponsor preferences to scope decisions.",
        ))
    return results


def _wont_chunks(doc: RequirementsDoc) -> list[Chunk]:
    scoped = [c for c in doc.chunks if any(_WONT_SECTION_RE.search(t) for t in c.section_path)]
    scoped += [c for c in doc.fr.values() if doc.priority(c) == "wont"]
    return scoped


def requirements_reference_checks(doc: RequirementsDoc, ref: dict[str, Any]) -> list[CheckResult]:
    """Check a BRD or PRD against expected requirements and exclusions."""
    spec = ref.get(doc.kind) or {}
    results: list[CheckResult] = []
    frs = [(fr, chunk) for fr, chunk in doc.fr.items()]

    if must_cover := spec.get("must_cover"):
        items = []
        for need in must_cover:
            patterns = compile_patterns(need["all_of"])
            matches = [(fr, c) for fr, c in frs if _all_of(c.text, patterns)]
            musts = [fr for fr, c in matches if doc.priority(c) == "must"]
            score = 1.0 if musts else (0.5 if matches else 0.0)
            shown = musts or [fr for fr, _ in matches]
            items.append({
                "id": need["id"], "label": need["label"], "score": score,
                "evidence": (", ".join(shown[:3]) + ("" if musts else " (not Must)")) if matches else "",
            })
        results.append(_items_check(
            f"ref.{doc.kind}.must_cover", "Critical needs captured as Must-haves", items,
            "Each critical research need should appear as a Must requirement.", 2.0,
        ))

    if expected_wont := spec.get("expected_wont"):
        wont_text = [c for c in _wont_chunks(doc)]
        items = []
        for item in expected_wont:
            pattern = re.compile(item["pattern"], re.I)
            hit = next((c for c in wont_text if pattern.search(c.text)), None)
            items.append({
                "id": item["id"], "label": item["label"], "score": 1.0 if hit else 0.0,
                "evidence": hit.snippet(70) if hit else "",
            })
        results.append(_items_check(
            f"ref.{doc.kind}.expected_wont", "Out-of-scope items explicitly excluded", items,
            "Record exclusions in a Won't-have list so they do not silently return.",
        ))

    if forbidden := spec.get("forbidden_must"):
        items = []
        for item in forbidden:
            pattern = re.compile(item["pattern"], re.I)
            offenders = [fr for fr, c in frs if doc.priority(c) == "must" and pattern.search(doc.statement(c))]
            items.append({
                "id": item["id"], "label": item["label"], "score": 0.0 if offenders else 1.0,
                "evidence": f"Must in {', '.join(offenders)}" if offenders else "",
            })
        results.append(_items_check(
            f"ref.{doc.kind}.forbidden_must", "No rejected ideas promoted to Must", items,
            "Ideas stakeholders rejected must not return as Must requirements.",
        ))
    return results
