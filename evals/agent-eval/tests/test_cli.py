# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""End-to-end CLI tests, including an optional run on the real dry-run artifacts."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from conftest import GOOD_BRD, REFERENCE, WEAK_BRD

from agent_eval.cli import EXIT_ERROR, EXIT_FAILURE, EXIT_SUCCESS, main
from agent_eval.report import render_comparison

PROJECT = Path(__file__).resolve().parents[1]
TRACKING = PROJECT.parents[1] / ".copilot-tracking"
DRY_RUN_DT = TRACKING / "dt" / "apex-titan-watch"
DRY_RUN_BRD = TRACKING / "brd-sessions" / "apex-titan-watch-brd.md"
APEX_REF = PROJECT / "references" / "apex-titan-watch.yaml"


@pytest.fixture()
def reference_file(write_file):
    import yaml

    return write_file("ref.yaml", yaml.safe_dump(REFERENCE))


def _run(tmp_path, *args) -> tuple[int, dict]:
    out = tmp_path / "results"
    code = main(["run", "--out", str(out), *args])
    files = sorted(out.glob("*.json"))
    return code, (json.loads(files[-1].read_text(encoding="utf-8")) if files else {})


def test_given_no_inputs_when_run_then_exits_with_config_error(tmp_path):
    # Act
    code = main(["run", "--out", str(tmp_path)])

    # Assert
    assert code == EXIT_ERROR


def test_given_artifacts_when_run_then_writes_json_and_markdown(tmp_path, dt_folder, write_file, reference_file):
    # Arrange
    brd = write_file("good.md", GOOD_BRD)

    # Act
    code, data = _run(tmp_path, "--label", "team-a", "--dt", str(dt_folder), "--brd", str(brd),
                      "--reference", str(reference_file))

    # Assert
    assert code == EXIT_SUCCESS and {a["kind"] for a in data["artifacts"]} == {"dt", "brd"}
    assert list((tmp_path / "results").glob("team-a-*.md"))


def test_given_weak_brd_and_fail_under_when_run_then_exits_failure(tmp_path, write_file):
    # Arrange
    brd = write_file("weak.md", WEAK_BRD)

    # Act
    code, _ = _run(tmp_path, "--brd", str(brd), "--fail-under", "0.8")

    # Assert
    assert code == EXIT_FAILURE


def test_given_two_runs_when_compared_then_regression_detected(tmp_path, write_file):
    # Arrange
    _, good = _run(tmp_path, "--label", "good", "--brd", str(write_file("g.md", GOOD_BRD)))
    _, weak = _run(tmp_path, "--label", "weak", "--brd", str(write_file("w.md", WEAK_BRD)))
    paths = [write_file("good.json", json.dumps(good)), write_file("weak.json", json.dumps(weak))]

    # Act
    code = main(["compare", *map(str, paths), "--fail-on-regression", "--out", str(tmp_path / "cmp.md")])

    # Assert
    assert code == EXIT_FAILURE and "brd.fr_ac_coverage" in (tmp_path / "cmp.md").read_text(encoding="utf-8")


def test_given_two_runs_when_rendered_then_delta_column_present(tmp_path, write_file):
    # Arrange
    _, good = _run(tmp_path, "--label", "good", "--brd", str(write_file("g.md", GOOD_BRD)))

    # Act
    text = render_comparison([good, good])

    # Assert
    assert "| Δ |" in text and "None." in text


def test_given_dt_folder_when_prompt_then_lists_files_without_transcript(dt_folder, capsys):
    # Act
    code = main(["prompt", "dt", str(dt_folder)])

    # Assert
    out = capsys.readouterr().out
    assert code == EXIT_SUCCESS and "#file:" in out and "session-transcript" not in out


def test_given_path_inside_git_repo_when_workspace_relative_then_relative_to_repo_root(tmp_path):
    # Arrange
    from agent_eval.cli import workspace_relative

    (tmp_path / ".git").mkdir()
    target = tmp_path / ".copilot-tracking" / "brd-sessions" / "team-a-brd.md"
    target.parent.mkdir(parents=True)
    target.write_text("x", encoding="utf-8")

    # Act
    result = workspace_relative(str(target))

    # Assert
    assert result == ".copilot-tracking/brd-sessions/team-a-brd.md"


@pytest.mark.skipif(not (DRY_RUN_DT.is_dir() and DRY_RUN_BRD.is_file()), reason="dry-run artifacts not present")
def test_given_dry_run_artifacts_when_run_then_flags_unmeasurable_goals_and_covers_reference(tmp_path):
    # Act
    code, data = _run(tmp_path, "--dt", str(DRY_RUN_DT), "--brd", str(DRY_RUN_BRD), "--reference", str(APEX_REF))

    # Assert
    checks = {c["id"]: c for a in data["artifacts"] for c in a["checks"]}
    assert code == EXIT_SUCCESS
    assert checks["brd.goal_measurability"]["status"] == "fail"
    assert checks["ref.dt.themes"]["score"] == 1.0 and checks["ref.brd.forbidden_must"]["score"] == 1.0
