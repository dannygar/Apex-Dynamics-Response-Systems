# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Markdown and JSON reporting, plus multi-run comparison."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from agent_eval.models import EvalRun

ICON = {"pass": "✅", "warn": "⚠️", "fail": "❌", "skip": "➖"}
KIND_LABEL = {"dt": "Design Thinking", "brd": "BRD", "prd": "PRD"}
LAYER_LABEL = {"structure": "Structure & traceability", "reference": "Reference coverage", "judge": "AI judge"}


def _pct(value: float) -> str:
    return f"{value * 100:.0f}%"


def _cell(text: str) -> str:
    return " ".join(str(text).split()).replace("|", "\\|")


def write_json(run: EvalRun, path: Path) -> None:
    """Write the full run as JSON."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(run.to_dict(), indent=2, ensure_ascii=False), encoding="utf-8")


def render_run(run: EvalRun) -> str:
    """Render one run as a self-check style markdown report."""
    lines = [
        f"# Agent output evaluation: {run.label}",
        "",
        f"{ICON[run.status]} **Overall {_pct(run.score)}** · evaluated {run.created_at}"
        + (f" · reference `{run.reference_id}`" if run.reference_id else " · no reference file")
        + (f" · judge `{run.judge['deployment']}`" if run.judge else " · AI judge off"),
        "",
        "| Artifact | Score | " + " | ".join(LAYER_LABEL.values()) + " |",
        "|---|---|" + "---|" * len(LAYER_LABEL),
    ]
    for art in run.artifacts:
        layers = " | ".join(_pct(art.layer_scores[k]) if k in art.layer_scores else "—" for k in LAYER_LABEL)
        lines.append(f"| {KIND_LABEL[art.kind]} | {ICON[art.status]} {_pct(art.score)} | {layers} |")

    for art in run.artifacts:
        lines += ["", f"## {KIND_LABEL[art.kind]}", "", f"Source: `{art.source}`", "",
                  "| | Check | Score | Result |", "|---|---|---|---|"]
        for c in art.checks:
            score = "—" if c.status == "skip" else _pct(c.score)
            lines.append(f"| {ICON[c.status]} | {_cell(c.title)} `{c.id}` | {score} | {_cell(c.detail)} |")
        todo = sorted((c for c in art.checks if c.status in ("fail", "warn")), key=lambda c: (c.score, c.id))
        if todo:
            lines += ["", "### What to improve", ""]
            for c in todo:
                lines.append(f"* {ICON[c.status]} **{c.title}** ({_pct(c.score)}). {c.guidance}")
                lines += [f"  * {_cell(e)}" for e in c.evidence[:4] if not e.startswith("✔")]
        ref_checks = [c for c in art.checks if c.layer == "reference"]
        if ref_checks:
            lines += ["", "### Reference coverage detail", ""]
            for c in ref_checks:
                lines.append(f"**{c.title}**")
                lines.append("")
                lines += [f"* {_cell(e)}" for e in c.evidence]
                lines.append("")
    if run.judge:
        usage = run.judge.get("usage", {})
        lines += ["", "## Judge accounting", "",
                  f"Deployment `{run.judge['deployment']}`, {run.judge.get('samples', 1)} sample(s), "
                  f"{usage.get('calls', 0)} calls, {usage.get('prompt_tokens', 0):,} prompt tokens, "
                  f"{usage.get('completion_tokens', 0):,} completion tokens."]
    lines += ["", "> Scores support coaching conversations; they do not replace human review of the artifacts.", ""]
    return "\n".join(lines)


def load_runs(paths: list[Path]) -> list[dict[str, Any]]:
    """Load saved run JSON files."""
    return [json.loads(p.read_text(encoding="utf-8")) for p in paths]


def _check_scores(run: dict[str, Any]) -> dict[str, float | None]:
    scores: dict[str, float | None] = {}
    for art in run["artifacts"]:
        scores[f"{art['kind']}.overall"] = art["score"]
        for layer, value in art["layer_scores"].items():
            scores[f"{art['kind']}.layer.{layer}"] = value
        for check in art["checks"]:
            scores[check["id"]] = None if check["status"] == "skip" else check["score"]
    return scores


def regressions(baseline: dict[str, Any], candidate: dict[str, Any], tolerance: float) -> list[str]:
    """List checks where ``candidate`` scores more than ``tolerance`` below ``baseline``."""
    base, cand = _check_scores(baseline), _check_scores(candidate)
    found = []
    for key, value in base.items():
        other = cand.get(key)
        if value is not None and other is not None and value - other > tolerance:
            found.append(f"{key}: {_pct(value)} → {_pct(other)}")
    return found


def render_comparison(runs: list[dict[str, Any]], tolerance: float = 0.1) -> str:
    """Render a side-by-side comparison of several runs (teams or models)."""
    labels = [r["label"] for r in runs]
    scores = [_check_scores(r) for r in runs]
    keys: list[str] = []
    for s in scores:
        keys += [k for k in s if k not in keys]
    lines = ["# Agent output comparison", "", "| Run | Overall | Judge | Created |", "|---|---|---|---|"]
    for r in runs:
        judge = r["judge"]["deployment"] if r.get("judge") else "off"
        lines.append(f"| {r['label']} | {ICON[r['status']]} {_pct(r['score'])} | {judge} | {r['created_at']} |")
    lines += ["", "| Check | " + " | ".join(labels) + (" | Δ |" if len(runs) == 2 else " |"),
              "|---|" + "---|" * len(labels) + ("---|" if len(runs) == 2 else "")]
    for key in keys:
        values = [s.get(key) for s in scores]
        present = [v for v in values if v is not None]
        best = max(present) if present else None
        cells = []
        for v in values:
            if v is None:
                cells.append("—")
            else:
                cells.append(f"**{_pct(v)}**" if len(present) > 1 and v == best else _pct(v))
        row = f"| `{key}` | " + " | ".join(cells)
        if len(runs) == 2:
            delta = "—" if None in values else f"{(values[1] - values[0]) * 100:+.0f}"
            row += f" | {delta}"
        lines.append(row + " |")
    if len(runs) == 2:
        drops = regressions(runs[0], runs[1], tolerance)
        lines += ["", f"## Regressions ({labels[1]} vs {labels[0]}, tolerance {_pct(tolerance)})", ""]
        lines += [f"* {d}" for d in drops] or ["None."]
    lines.append("")
    return "\n".join(lines)
