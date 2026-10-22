# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Tests for BRD and PRD structural checks."""

from __future__ import annotations

import pytest
from conftest import GOOD_BRD, GOOD_PRD, WEAK_BRD

from agent_eval.artifacts import RequirementsDoc
from agent_eval.checks_requirements import run_requirements_checks


@pytest.fixture()
def good_brd(write_file, profile_config):
    return {c.id: c for c in run_requirements_checks(
        RequirementsDoc.load("brd", write_file("good.md", GOOD_BRD), profile_config["brd"]), profile_config)}


@pytest.fixture()
def weak_brd(write_file, profile_config):
    return {c.id: c for c in run_requirements_checks(
        RequirementsDoc.load("brd", write_file("weak.md", WEAK_BRD), profile_config["brd"]), profile_config)}


@pytest.mark.parametrize(
    "check_id",
    ["brd.fr_ac_coverage", "brd.fr_goal_coverage", "brd.goal_measurability", "brd.acceptance_form",
     "brd.id_hygiene", "brd.vague_terms", "brd.solution_leakage", "brd.requirement_sources"],
)
def test_given_good_brd_when_checked_then_core_checks_pass(good_brd, check_id):
    # Assert
    assert good_brd[check_id].status == "pass"


def test_given_fr_without_ac_when_checked_then_coverage_fails_and_names_fr(weak_brd):
    # Act
    result = weak_brd["brd.fr_ac_coverage"]

    # Assert
    assert result.status == "fail" and any("FR-002" in e for e in result.evidence)


def test_given_tbd_goals_when_checked_then_measurability_fails(weak_brd):
    # Assert
    assert weak_brd["brd.goal_measurability"].score == 0.0


def test_given_undefined_ac_reference_when_checked_then_id_hygiene_reports_dangling(weak_brd):
    # Assert
    assert any("AC-009" in e for e in weak_brd["brd.id_hygiene"].evidence)


def test_given_ui_words_in_brd_when_checked_then_solution_leakage_flagged(weak_brd):
    # Act
    result = weak_brd["brd.solution_leakage"]

    # Assert
    assert result.status == "fail" and any("dashboard" in e for e in result.evidence)


def test_given_vague_words_when_checked_then_vague_terms_flagged(weak_brd):
    # Assert
    assert any("fast" in e or "real-time" in e for e in weak_brd["brd.vague_terms"].evidence)


def test_given_all_musts_when_checked_then_moscow_reports_inflation(weak_brd):
    # Assert
    assert any("inflation" in e for e in weak_brd["brd.moscow"].evidence)


def test_given_non_testable_ac_when_checked_then_acceptance_form_fails(weak_brd):
    # Assert
    assert weak_brd["brd.acceptance_form"].status == "fail"


def test_given_placeholder_frontmatter_when_checked_then_frontmatter_not_pass(weak_brd):
    # Assert
    assert weak_brd["brd.frontmatter"].status != "pass"


def test_given_good_prd_when_checked_then_prd_specific_checks_pass(write_file, profile_config):
    # Arrange
    doc = RequirementsDoc.load("prd", write_file("prd.md", GOOD_PRD), profile_config["prd"])

    # Act
    results = {c.id: c for c in run_requirements_checks(doc, profile_config)}

    # Assert
    assert all(results[k].status == "pass" for k in
               ("prd.success_metrics", "prd.mvp_framing", "prd.brd_lineage", "prd.fr_ac_coverage"))


def test_given_prd_when_checked_then_solution_leakage_is_skipped(write_file, profile_config):
    # Arrange
    doc = RequirementsDoc.load("prd", write_file("prd.md", GOOD_PRD), profile_config["prd"])

    # Act
    results = {c.id: c for c in run_requirements_checks(doc, profile_config)}

    # Assert
    assert results["prd.solution_leakage"].status == "skip"
