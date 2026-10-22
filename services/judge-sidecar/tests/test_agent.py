# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Tests for the Foundry hosted-agent (Invocations protocol) entry point."""

from __future__ import annotations

import json
from typing import Any

import pytest
from starlette.testclient import TestClient

from judge_sidecar.agent import create_app
from tests.test_server import CALIBRATION_BRD, FakeJudge, make_evaluator


def _client(judge: Any | None = None) -> TestClient:
    return TestClient(create_app(make_evaluator(judge or FakeJudge())))


def test_contract_action_matches_sidecar_contract() -> None:
    evaluator = make_evaluator(FakeJudge())
    with TestClient(create_app(evaluator)) as client:
        response = client.post("/invocations", json={"action": "contract"})
    assert response.status_code == 200
    assert response.json() == evaluator.contract()


def test_evaluate_action_returns_sidecar_payload() -> None:
    body = {"action": "evaluate", "kind": "brd",
            "files": [{"path": "weak.md", "text": CALIBRATION_BRD.read_text("utf-8")}]}
    with _client() as client:
        response = client.post("/invocations", json=body)
    assert response.status_code == 200
    payload = response.json()
    assert payload["kind"] == "brd" and set(payload["layer_scores"]) == {"structure", "reference", "judge"}
    assert payload["contract_version"].startswith("agent-eval/")


@pytest.mark.parametrize(("body", "status"), [
    (b"not json", 400),
    (b"[]", 400),
    (json.dumps({"action": "other"}).encode(), 400),
    (json.dumps({"action": "evaluate", "kind": "xyz", "files": []}).encode(), 400),
    (b"", 400),
])
def test_invalid_input_rejected(body: bytes, status: int) -> None:
    with _client() as client:
        response = client.post("/invocations", content=body, headers={"Content-Type": "application/json"})
    assert response.status_code == status
    assert "error" in response.json()


def test_judge_failure_maps_to_502_without_details() -> None:
    body = {"action": "evaluate", "kind": "prd", "files": [{"path": "p.md", "text": "# PRD\n\nA requirement line."}]}
    with _client(FakeJudge(fail=True)) as client:
        response = client.post("/invocations", json=body)
    assert response.status_code == 502
    assert "secret" not in response.text
