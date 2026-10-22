# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Result models and scoring rules shared by every evaluation layer."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any, Literal

Status = Literal["pass", "warn", "fail", "skip"]
Layer = Literal["structure", "reference", "judge"]
ArtifactKind = Literal["dt", "brd", "prd"]

PASS_THRESHOLD = 0.8
WARN_THRESHOLD = 0.5

DEFAULT_LAYER_WEIGHTS: dict[str, float] = {"structure": 0.4, "reference": 0.3, "judge": 0.3}


def status_for(score: float) -> Status:
    """Map a 0..1 score to a status band."""
    if score >= PASS_THRESHOLD:
        return "pass"
    if score >= WARN_THRESHOLD:
        return "warn"
    return "fail"


@dataclass
class CheckResult:
    """Outcome of one evaluation check.

    Attributes:
        id: Stable check identifier, e.g. ``brd.fr_ac_coverage``.
        layer: Evaluation layer that produced the check.
        title: Short human-readable name.
        score: Normalized score in the range 0..1.
        status: Band derived from ``score`` unless explicitly skipped.
        weight: Relative weight inside its layer.
        detail: One-line explanation of the measured value.
        evidence: Supporting snippets or offending items.
        guidance: What the team should do to improve this check.
        data: Extra machine-readable values for reports and comparisons.
    """

    id: str
    layer: Layer
    title: str
    score: float
    status: Status = "pass"
    weight: float = 1.0
    detail: str = ""
    evidence: list[str] = field(default_factory=list)
    guidance: str = ""
    data: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def scored(cls, score: float, **kwargs: Any) -> CheckResult:
        """Create a result whose status is derived from the score."""
        bounded = max(0.0, min(1.0, score))
        return cls(score=bounded, status=status_for(bounded), **kwargs)

    @classmethod
    def skipped(cls, **kwargs: Any) -> CheckResult:
        """Create a result that does not count toward the layer score."""
        return cls(score=0.0, status="skip", weight=0.0, **kwargs)


@dataclass
class ArtifactResult:
    """All checks for one artifact kind plus its rolled-up scores."""

    kind: ArtifactKind
    source: str
    checks: list[CheckResult] = field(default_factory=list)
    layer_scores: dict[str, float] = field(default_factory=dict)
    score: float = 0.0
    status: Status = "fail"

    def finalize(self, layer_weights: dict[str, float] | None = None) -> None:
        """Compute layer scores and the weighted artifact score.

        Layers without any scored checks are excluded and the remaining
        layer weights are renormalized.
        """
        weights = layer_weights or DEFAULT_LAYER_WEIGHTS
        self.layer_scores = {}
        for layer in weights:
            scored = [c for c in self.checks if c.layer == layer and c.status != "skip" and c.weight > 0]
            if scored:
                total_weight = sum(c.weight for c in scored)
                self.layer_scores[layer] = sum(c.score * c.weight for c in scored) / total_weight
        active = {k: w for k, w in weights.items() if k in self.layer_scores}
        if active:
            self.score = sum(self.layer_scores[k] * w for k, w in active.items()) / sum(active.values())
        else:
            self.score = 0.0
        self.status = status_for(self.score)
        if self.status == "pass" and any(c.status == "fail" for c in self.checks):
            self.status = "warn"


@dataclass
class EvalRun:
    """A complete evaluation run for one team, model, or configuration."""

    label: str
    created_at: str
    artifacts: list[ArtifactResult] = field(default_factory=list)
    reference_id: str | None = None
    judge: dict[str, Any] | None = None
    inputs: dict[str, Any] = field(default_factory=dict)
    score: float = 0.0
    status: Status = "fail"

    def finalize(self) -> None:
        """Average artifact scores into the run score."""
        if self.artifacts:
            self.score = sum(a.score for a in self.artifacts) / len(self.artifacts)
        self.status = status_for(self.score)
        if self.status == "pass" and any(a.status != "pass" for a in self.artifacts):
            self.status = "warn"

    def to_dict(self) -> dict[str, Any]:
        """Serialize the run for JSON output."""
        return asdict(self)
