# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Tests for Design Thinking structural checks and reference coverage."""

from __future__ import annotations

from conftest import GOOD_BRD, REFERENCE, WEAK_BRD

from agent_eval.artifacts import DtBundle, RequirementsDoc
from agent_eval.checks_dt import run_dt_checks
from agent_eval.reference import dt_reference_checks, requirements_reference_checks


def _dt(folder, profile_config, methods=None):
    bundle = DtBundle.load(folder, profile_config["dt"])
    return bundle, {c.id: c for c in run_dt_checks(bundle, profile_config, methods, REFERENCE["citation_patterns"])}


def test_given_complete_dt_bundle_when_checked_then_artifacts_and_hmw_pass(dt_folder, profile_config):
    # Act
    _, results = _dt(dt_folder, profile_config)

    # Assert
    assert all(results[k].status == "pass" for k in ("dt.artifacts", "dt.hmw_count", "dt.hmw_neutral", "dt.evidence"))


def test_given_uncited_theme_when_checked_then_evidence_flags_it(dt_folder, profile_config):
    # Arrange
    clusters = dt_folder / "method-03-synthesis" / "affinity-clusters.md"
    text = clusters.read_text(encoding="utf-8").replace(
        "| T5 |", "| T6 | People generally want better tools and a nicer experience overall | n/a |\n| T5 |")
    clusters.write_text(text, encoding="utf-8")

    # Act
    _, results = _dt(dt_folder, profile_config)

    # Assert
    assert any("nicer experience" in e for e in results["dt.evidence"].evidence)


def test_given_transcript_when_loaded_then_excluded_from_analysis(dt_folder, profile_config):
    # Act
    bundle, _ = _dt(dt_folder, profile_config)

    # Assert
    assert "session-transcript.md" not in bundle.files


def test_given_missing_method_file_when_checked_then_artifacts_names_it(dt_folder, profile_config):
    # Arrange
    (dt_folder / "method-03-synthesis" / "insight-statements.md").unlink()

    # Act
    _, results = _dt(dt_folder, profile_config)

    # Assert
    assert any("insight-statements" in e for e in results["dt.artifacts"].evidence)


def test_given_solution_in_hmw_when_checked_then_hmw_neutral_flags_it(dt_folder, profile_config):
    # Arrange
    hmw = dt_folder / "method-03-synthesis" / "how-might-we-questions.md"
    hmw.write_text(hmw.read_text(encoding="utf-8") + "| HMW6 | ...add a dashboard button for silence? |\n",
                   encoding="utf-8")

    # Act
    _, results = _dt(dt_folder, profile_config)

    # Assert
    assert results["dt.hmw_neutral"].score < 1.0 and any("dashboard" in e for e in results["dt.hmw_neutral"].evidence)


def test_given_methods_1_only_when_checked_then_synthesis_checks_not_run(dt_folder, profile_config):
    # Act
    _, results = _dt(dt_folder, profile_config, methods=[1])

    # Assert
    assert "dt.hmw_count" not in results and results["dt.artifacts"].status == "pass"


def test_given_reference_when_dt_checked_then_missing_theme_and_stakeholder_reported(dt_folder, profile_config):
    # Arrange
    bundle = DtBundle.load(dt_folder, profile_config["dt"])

    # Act
    results = {c.id: c for c in dt_reference_checks(bundle, REFERENCE)}

    # Assert
    assert results["ref.dt.themes"].score == 0.5 and results["ref.dt.stakeholders"].score < 1.0


def test_given_clipzilla_theme_when_reference_checked_then_not_themes_fails(dt_folder, profile_config):
    # Arrange
    clusters = dt_folder / "method-03-synthesis" / "affinity-clusters.md"
    text = clusters.read_text(encoding="utf-8").replace(
        "| T5 |", "| T6 | Clipzilla mascot delights the steering group | sponsor (D09) |\n| T5 |")
    clusters.write_text(text, encoding="utf-8")
    bundle = DtBundle.load(dt_folder, profile_config["dt"])

    # Act
    results = {c.id: c for c in dt_reference_checks(bundle, REFERENCE)}

    # Assert
    assert results["ref.dt.not_themes"].status == "fail"


def test_given_good_brd_when_reference_checked_then_all_groups_pass(write_file, profile_config):
    # Arrange
    doc = RequirementsDoc.load("brd", write_file("good.md", GOOD_BRD), profile_config["brd"])

    # Act
    results = {c.id: c for c in requirements_reference_checks(doc, REFERENCE)}

    # Assert
    assert results["ref.brd.forbidden_must"].status == "pass" and results["ref.brd.must_cover"].score == 0.75


def test_given_weak_brd_when_reference_checked_then_forbidden_musts_fail(write_file, profile_config):
    # Arrange
    doc = RequirementsDoc.load("brd", write_file("weak.md", WEAK_BRD), profile_config["brd"])

    # Act
    results = {c.id: c for c in requirements_reference_checks(doc, REFERENCE)}

    # Assert
    assert results["ref.brd.forbidden_must"].score == 0.0 and results["ref.brd.expected_wont"].score == 0.0
