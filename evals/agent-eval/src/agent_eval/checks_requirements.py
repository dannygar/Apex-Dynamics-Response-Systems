# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Deterministic structure and traceability checks for BRD and PRD documents."""

from __future__ import annotations

import re
from collections.abc import Callable
from typing import Any

from agent_eval.artifacts import RequirementsDoc
from agent_eval.config import compile_patterns, first_match
from agent_eval.markdown import ID_RE, chunks_in, duplicate_definitions, headings, ids_in
from agent_eval.models import CheckResult

_UNMEASURED_RE = re.compile(r"^\W*(tbd|to be|n/?a|unknown|none|pending|-+)\b", re.I)
_CITATION_RE = re.compile(
    r"\b(source|rationale|evidence)\s*:|\[[^\]]+\]\([^)]+\)|\([A-Z]{1,4}\d{1,3}\b|\"[^\"]{8,}\"", re.I
)
_ID_PREFIXES = ("FR", "AC", "NFR", "BR", "CON")


def _check(doc: RequirementsDoc, name: str, title: str, score: float, **kwargs: Any) -> CheckResult:
    return CheckResult.scored(score, id=f"{doc.kind}.{name}", layer="structure", title=title, **kwargs)


def check_frontmatter(doc: RequirementsDoc, profile: dict[str, Any]) -> CheckResult:
    required = profile["frontmatter"]
    missing = [
        key for key in required if not doc.frontmatter.get(key) or "{{" in str(doc.frontmatter.get(key))
    ]
    score = 1 - len(missing) / len(required) if required else 1.0
    return _check(
        doc, "frontmatter", "Frontmatter metadata complete", score,
        detail=f"{len(required) - len(missing)}/{len(required)} required fields set",
        evidence=[f"missing: {k}" for k in missing],
        guidance="Ask the builder to fill document metadata (ID, version, owners, goal IDs).",
    )


def check_sections(doc: RequirementsDoc, profile: dict[str, Any]) -> CheckResult:
    titles = [title.lower() for _, title in headings(doc.text)]
    required = profile["sections"]
    missing = [s for s in required if not any(t.startswith(s.lower()) for t in titles)]
    score = 1 - len(missing) / len(required)
    return _check(
        doc, "sections", "Template sections present", score,
        detail=f"{len(required) - len(missing)}/{len(required)} canonical sections",
        evidence=[f"missing section: {s}" for s in missing],
        guidance="Resume the builder and ask it to complete the missing template sections.",
    )


def check_placeholders(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    hits = [line.strip() for line in doc.text.splitlines() if "{{" in line or line.strip().startswith("*Guidance*")]
    score = max(0.0, 1 - len(hits) / 10)
    return _check(
        doc, "placeholders", "No leftover template placeholders", score,
        detail=f"{len(hits)} placeholder or guidance lines remain",
        evidence=hits[:5],
        guidance="Replace {{placeholders}} and remove template *Guidance* lines.",
    )


def check_id_hygiene(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    issues: list[str] = []
    for prefix, section in (("FR", r"^functional requirements"), ("AC", r"^acceptance criteria")):
        issues += [f"duplicate definition: {i}" for i in duplicate_definitions(doc.chunks, prefix, section)]
    prefixes = (*_ID_PREFIXES, doc.goal_prefix)
    referenced = set(ids_in(doc.text, prefixes))
    defined = set().union(*(doc.defined_ids(p) for p in prefixes))
    dangling = sorted(referenced - defined)
    issues += [f"referenced but never defined: {i}" for i in dangling]
    malformed = sorted({
        m.group(0) for m in ID_RE.finditer(doc.text) if len(m.group(2)) != 3 and m.group(1) in prefixes
    })
    issues += [f"non 3-digit ID: {i}" for i in malformed]
    total = max(len(referenced), 1)
    return _check(
        doc, "id_hygiene", "IDs unique, defined, and well-formed", 1 - min(1.0, len(issues) / total),
        detail=f"{len(issues)} ID issues across {len(referenced)} referenced IDs",
        evidence=issues[:10],
        guidance="Define every referenced ID once using the canonical PREFIX-### form.",
    )


def _coverage(doc: RequirementsDoc, target: str, threshold: float, name: str, title: str) -> CheckResult:
    if not doc.fr:
        return _check(doc, name, title, 0.0, detail="No FR-### requirements found",
                      guidance="Functional requirements are missing; coverage cannot be computed.")
    defined_targets = doc.defined_ids(target)
    uncovered = [fr for fr in doc.fr if not (doc.links(fr, target) & defined_targets)]
    pct = 100 * (len(doc.fr) - len(uncovered)) / len(doc.fr)
    return _check(
        doc, name, title, min(1.0, pct / threshold) if threshold else 1.0,
        detail=f"{pct:.0f}% of {len(doc.fr)} FRs linked to a defined {target} (threshold {threshold:.0f}%)",
        evidence=[f"{fr} has no {target}" for fr in uncovered],
        guidance=f"Link every FR to at least one defined {target}-### item in the traceability matrix.",
        data={"coverage_pct": round(pct, 1), "threshold_pct": threshold},
    )


def check_fr_ac_coverage(doc: RequirementsDoc, profile: dict[str, Any]) -> CheckResult:
    threshold = float(doc.frontmatter.get(profile["coverage_threshold_key"]) or 80.0)
    return _coverage(doc, "AC", threshold, "fr_ac_coverage", "FR → acceptance criteria coverage")


def check_fr_goal_coverage(doc: RequirementsDoc, profile: dict[str, Any]) -> CheckResult:
    key = profile.get("goal_coverage_threshold_key")
    threshold = float(doc.frontmatter.get(key) or 100.0) if key else 100.0
    return _coverage(doc, doc.goal_prefix, threshold, "fr_goal_coverage", f"FR → {doc.goal_prefix} alignment")


def check_acceptance_form(doc: RequirementsDoc, _: dict[str, Any], rules: dict[str, Any]) -> CheckResult:
    if not doc.ac:
        return _check(doc, "acceptance_form", "Acceptance criteria are testable", 0.0,
                      detail="No AC-### items found",
                      guidance="Write AC-### items in Given/When/Then or EARS form.")
    patterns = compile_patterns(rules["acceptance_form"])
    weak = [ac for ac, chunk in doc.ac.items() if not first_match(chunk.text, patterns)]
    return _check(
        doc, "acceptance_form", "Acceptance criteria are testable", 1 - len(weak) / len(doc.ac),
        detail=f"{len(doc.ac) - len(weak)}/{len(doc.ac)} AC items use Given/When/Then or EARS form",
        evidence=[f"{ac}: {doc.ac[ac].snippet(100)}" for ac in weak[:6]],
        guidance="Rewrite each AC as 'Given…, When…, Then…' or 'When…, the system shall…'.",
    )


def check_moscow(doc: RequirementsDoc, _: dict[str, Any], rules: dict[str, Any]) -> CheckResult:
    if not doc.fr:
        return _check(doc, "moscow", "MoSCoW prioritization", 0.0, detail="No FR-### requirements found")
    priorities = {fr: doc.priority(chunk) for fr, chunk in doc.fr.items()}
    missing = [fr for fr, p in priorities.items() if p is None]
    musts = sum(1 for p in priorities.values() if p == "must")
    share = musts / len(priorities)
    limit = float(rules["max_must_share"])
    inflation_penalty = 0.0 if share <= limit else min(1.0, (share - limit) / (1 - limit))
    score = 0.5 * (1 - len(missing) / len(priorities)) + 0.5 * (1 - inflation_penalty)
    evidence = [f"{fr} has no MoSCoW priority" for fr in missing]
    if share > limit:
        evidence.append(f"Must share {share:.0%} exceeds {limit:.0%} inflation limit")
    return _check(
        doc, "moscow", "MoSCoW prioritization", score,
        detail=f"{musts}/{len(priorities)} Must ({share:.0%}); {len(missing)} unprioritized",
        evidence=evidence[:8],
        guidance="Give every FR a MoSCoW priority and keep Must-haves at or below about 60%.",
        data={"must_share": round(share, 3)},
    )


def _term_check(doc: RequirementsDoc, name: str, title: str, items: dict[str, str],
                patterns: list[str], guidance: str) -> CheckResult:
    compiled = compile_patterns(patterns)
    offenders = [(i, hit) for i, text in items.items() if (hit := first_match(text, compiled))]
    score = 1 - len(offenders) / len(items) if items else 1.0
    return _check(
        doc, name, title, score,
        detail=f"{len(items) - len(offenders)}/{len(items)} items clean",
        evidence=[f"{i}: '{hit}'" for i, hit in offenders[:10]],
        guidance=guidance,
    )


def check_vague_terms(doc: RequirementsDoc, _: dict[str, Any], rules: dict[str, Any]) -> CheckResult:
    items = {i: doc.statement(c) for i, c in {**doc.fr, **doc.nfr}.items()}
    items.update({i: c.text for i, c in doc.ac.items()})
    return _term_check(doc, "vague_terms", "Unambiguous wording", items, rules["vague_terms"],
                       "Replace vague words (fast, easy, robust, real-time…) with measurable conditions.")


def check_solution_leakage(doc: RequirementsDoc, profile: dict[str, Any], rules: dict[str, Any]) -> CheckResult:
    if not profile.get("solution_agnostic"):
        return CheckResult.skipped(id=f"{doc.kind}.solution_leakage", layer="structure",
                                   title="Solution-agnostic requirements", detail="Not required for this artifact")
    items = {i: doc.statement(c) for i, c in doc.fr.items()}
    return _term_check(doc, "solution_leakage", "Solution-agnostic requirements", items, rules["solution_terms"],
                       "State business needs, not UI or technology (no screens, buttons, dashboards, APIs).")


def _field(chunk_text: str, name: str) -> str | None:
    m = re.search(rf"^\W*{name}\W*[:|]\s*(.+)$", chunk_text, re.I | re.M)
    return m.group(1).strip() if m else None


def _measurable(value: str | None) -> bool:
    return bool(value) and not _UNMEASURED_RE.match(value) and bool(re.search(r"\d", value))


def check_goal_measurability(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    if not doc.goals:
        return _check(doc, "goal_measurability", "Goals have baseline and target", 0.0,
                      detail=f"No {doc.goal_prefix}-### goals found",
                      guidance=f"Define {doc.goal_prefix}-### goals with KPI, baseline, target, and timeframe.")
    weak: list[str] = []
    for goal, chunk in doc.goals.items():
        baseline = chunk.cell("baseline") or _field(chunk.text, "baseline")
        target = chunk.cell("target") or _field(chunk.text, "target")
        if not (_measurable(baseline) and _measurable(target)):
            weak.append(f"{goal}: baseline={(baseline or 'missing')[:40]!r}, target={(target or 'missing')[:40]!r}")
    return _check(
        doc, "goal_measurability", "Goals have baseline and target", 1 - len(weak) / len(doc.goals),
        detail=f"{len(doc.goals) - len(weak)}/{len(doc.goals)} goals have numeric baseline and target",
        evidence=weak[:8],
        guidance="Record a measured baseline and numeric target per goal, or an explicit plan to measure it. "
                 "Do not invent numbers to pass this check.",
    )


def check_nfr_categories(doc: RequirementsDoc, profile: dict[str, Any]) -> CheckResult:
    categories = profile["nfr_categories"]
    addressed = 0.0
    gaps: list[str] = []
    for category in categories:
        scoped = chunks_in(doc.chunks, rf"^{re.escape(category)}")
        text = "\n".join(c.text for c in scoped)
        if ids_in(text, ("NFR",)):
            addressed += 1
        elif re.search(r"no separate|not applicable|n/a|out of scope|covered by", text, re.I):
            addressed += 0.5
            gaps.append(f"{category}: scoped out with rationale")
        else:
            gaps.append(f"{category}: no NFR")
    return _check(
        doc, "nfr_categories", "Non-functional categories addressed", addressed / len(categories),
        detail=f"{addressed:g}/{len(categories)} categories carry an NFR",
        evidence=gaps[:10],
        guidance="Add at least one verifiable NFR-### per quality category, or justify why it does not apply.",
    )


def check_requirement_sources(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    if not doc.fr:
        return _check(doc, "requirement_sources", "Requirements cite their evidence", 0.0, detail="No FRs")
    unsourced = [
        fr for fr, chunk in doc.fr.items()
        if not (chunk.cell("source", "rationale", "evidence") or _CITATION_RE.search(chunk.text))
    ]
    return _check(
        doc, "requirement_sources", "Requirements cite their evidence", 1 - len(unsourced) / len(doc.fr),
        detail=f"{len(doc.fr) - len(unsourced)}/{len(doc.fr)} FRs cite a source or rationale",
        evidence=[f"{fr} has no source" for fr in unsourced[:8]],
        guidance="Trace each requirement to the interview, document, or decision it came from.",
    )


def check_prd_success_metrics(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    scoped = chunks_in(doc.chunks, r"^success metrics")
    text = "\n".join(c.text for c in scoped)
    parts = {
        "metrics defined": bool(text.strip()),
        "baseline stated": bool(re.search(r"baseline", text, re.I)),
        "target stated": bool(re.search(r"target", text, re.I)),
        "linked to GOAL": bool(ids_in(text, ("GOAL",))),
    }
    missing = [k for k, ok in parts.items() if not ok]
    return _check(
        doc, "success_metrics", "Success metrics are measurable", 1 - len(missing) / len(parts),
        detail=f"{len(parts) - len(missing)}/{len(parts)} success-metric elements present",
        evidence=[f"missing: {m}" for m in missing],
        guidance="For each GOAL define metric, baseline, target, window, and data source.",
    )


def check_prd_mvp(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    scoped = chunks_in(doc.chunks, r"^mvp")
    text = "\n".join(c.text for c in scoped)
    referenced = set(ids_in(text, ("FR",)))
    score = 0.0 if not text.strip() else (1.0 if referenced else 0.5)
    return _check(
        doc, "mvp_framing", "MVP cut line names requirements", score,
        detail=f"MVP section references {len(referenced)} FRs" if text.strip() else "No MVP section content",
        guidance="State which FR-### items are in the first release and why the cut line sits there.",
    )


def check_prd_lineage(doc: RequirementsDoc, _: dict[str, Any]) -> CheckResult:
    has_brd = bool(doc.frontmatter.get("source_brd_id")) or bool(re.search(r"\bBRD[-_ ]", doc.text))
    return _check(
        doc, "brd_lineage", "PRD traces to the approved BRD", 1.0 if has_brd else 0.4,
        detail="source_brd_id set" if has_brd else "No source BRD reference",
        guidance="Point the PRD Builder at the BRD handoff file so business goals carry forward.",
    )


_COMMON: list[Callable[..., CheckResult]] = [
    check_frontmatter, check_sections, check_placeholders, check_id_hygiene,
    check_fr_ac_coverage, check_fr_goal_coverage, check_goal_measurability,
    check_nfr_categories, check_requirement_sources,
]
_WITH_RULES: list[Callable[..., CheckResult]] = [
    check_acceptance_form, check_moscow, check_vague_terms, check_solution_leakage,
]
_PRD_ONLY: list[Callable[..., CheckResult]] = [check_prd_success_metrics, check_prd_mvp, check_prd_lineage]


def run_requirements_checks(doc: RequirementsDoc, profiles: dict[str, Any]) -> list[CheckResult]:
    """Run every structural check for a BRD or PRD.

    Args:
        doc: Loaded requirements document.
        profiles: Bundled profile configuration.

    Returns:
        Structural check results in a stable order.
    """
    profile = profiles[doc.kind]
    rules = profiles["requirements"]
    results = [check(doc, profile) for check in _COMMON]
    results += [check(doc, profile, rules) for check in _WITH_RULES]
    if doc.kind == "prd":
        results += [check(doc, profile) for check in _PRD_ONLY]
    return results
