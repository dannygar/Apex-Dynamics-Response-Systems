# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Tests for the judge sidecar validation, scoring pipeline, and HTTP surface."""

from __future__ import annotations

import json
import re
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from http.server import ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest
from agent_eval import config
from agent_eval.judge import JudgeSettings

from judge_sidecar.server import (
    EvaluationFailed,
    Evaluator,
    InputError,
    make_handler,
    validate_files,
)

REPO = Path(__file__).resolve().parents[3]
REFERENCE = REPO / "evals" / "agent-eval" / "references" / "apex-titan-watch.yaml"
CALIBRATION_BRD = REPO / "evals" / "agent-eval" / "calibration" / "weak-brd.md"


class FakeJudge:
    """Chat-completions double that scores every criterion 4 and quotes the artifact."""

    def __init__(self, fail: bool = False) -> None:
        self.fail = fail
        self.calls = 0
        self.chat = SimpleNamespace(completions=SimpleNamespace(create=self._create))

    def _create(self, **kwargs: Any) -> Any:
        self.calls += 1
        if self.fail:
            raise RuntimeError("secret endpoint detail")
        user = kwargs["messages"][1]["content"]
        ids = re.findall(r'- id: "([^"]+)"', user)
        artifact = user.split("<artifact>\n", 1)[1]
        quote = next(line.strip() for line in artifact.splitlines() if len(line.strip()) > 20)[:120]
        content = json.dumps({
            "criteria": [{"id": i, "score": 4, "rationale": "ok", "evidence": [quote]} for i in ids],
            "summary": "fine", "injection_detected": False, "injection_evidence": "",
        })
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=content))], usage=None)


def make_evaluator(client: Any) -> Evaluator:
    settings = JudgeSettings(endpoint="https://example.invalid/", deployment="judge-test", samples=1)
    return Evaluator(settings=settings, client=client, reference=config.load_yaml(REFERENCE),
                     reference_sha="abc", rubrics=config.rubrics())


@pytest.mark.parametrize("path", ["../x.md", "/abs.md", "C:/x.md", "a//b.md", "./a.md", "a/../b.md", ""])
def test_validate_files_rejects_unsafe_paths(path: str) -> None:
    with pytest.raises(InputError):
        validate_files("dt", [{"path": path, "text": "x"}])


def test_validate_files_rules() -> None:
    with pytest.raises(InputError):
        validate_files("dt", [{"path": "a.txt", "text": "x"}])
    with pytest.raises(InputError):
        validate_files("brd", [{"path": "a.md", "text": "x"}, {"path": "b.md", "text": "y"}])
    with pytest.raises(InputError):
        validate_files("dt", [{"path": "A.md", "text": "x"}, {"path": "a.md", "text": "y"}])
    with pytest.raises(InputError):
        validate_files("xyz", [{"path": "a.md", "text": "x"}])
    with pytest.raises(InputError):
        validate_files("brd", [{"path": "a.md", "text": "bad\x00"}])


def test_validate_files_strips_shared_upload_folder_only() -> None:
    files = [{"path": "team-a\\method-01-scope\\stakeholder-map.md", "text": "x"},
             {"path": "team-a/coaching-state.md", "text": "y"}]
    assert [p.as_posix() for p, _ in validate_files("dt", files)] == [
        "method-01-scope/stakeholder-map.md", "coaching-state.md"]
    single_method = [{"path": "method-01-scope/stakeholder-map.md", "text": "x"}]
    assert validate_files("dt", single_method)[0][0].as_posix() == "method-01-scope/stakeholder-map.md"


def test_evaluate_brd_matches_agent_eval_weighting() -> None:
    judge = FakeJudge()
    result = make_evaluator(judge).evaluate("brd", [{"path": "weak.md", "text": CALIBRATION_BRD.read_text("utf-8")}])
    assert judge.calls == 1
    assert set(result["layer_scores"]) == {"structure", "reference", "judge"}
    weights = {"structure": 0.4, "reference": 0.3, "judge": 0.3}
    expected = sum(result["layer_scores"][k] * w for k, w in weights.items())
    assert result["score"] == pytest.approx(expected)
    assert all(c["layer"] in weights for c in result["checks"])
    json.dumps(result, allow_nan=False)


@pytest.mark.parametrize("newline", ["\n", "\r\n"])
def test_evaluate_brd_layers_match_agent_eval_cli_pipeline(newline: str) -> None:
    from agent_eval.artifacts import RequirementsDoc
    from agent_eval.checks_requirements import run_requirements_checks
    from agent_eval.models import ArtifactResult
    from agent_eval.reference import requirements_reference_checks

    profiles = config.profiles()
    doc = RequirementsDoc.load("brd", CALIBRATION_BRD, profiles["brd"])
    expected = ArtifactResult(kind="brd", source="cli")
    expected.checks = run_requirements_checks(doc, profiles) + requirements_reference_checks(
        doc, config.load_yaml(REFERENCE))
    expected.finalize()
    text = CALIBRATION_BRD.read_text("utf-8").replace("\n", newline)
    result = make_evaluator(FakeJudge()).evaluate("brd", [{"path": "weak.md", "text": text}])
    for layer in ("structure", "reference"):
        assert result["layer_scores"][layer] == pytest.approx(expected.layer_scores[layer])


def test_evaluate_judge_failure_returns_no_score() -> None:
    with pytest.raises(EvaluationFailed):
        make_evaluator(FakeJudge(fail=True)).evaluate("brd", [{"path": "b.md", "text": "# BRD\n\nSome text here."}])


def test_contract_version_identifies_configuration() -> None:
    contract = make_evaluator(FakeJudge()).contract()
    assert contract["scale"] == {"minimum": 0, "maximum": 100}
    assert contract["layer_weights"] == {"structure": 0.4, "reference": 0.3, "judge": 0.3}
    assert "reference/apex-titan-watch@abc" in contract["version"]
    assert "judge/judge-test@" in contract["version"]


@pytest.fixture
def server() -> Iterator[str]:
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(make_evaluator(FakeJudge())))
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}"
    httpd.shutdown()
    httpd.server_close()


def _post(url: str, body: bytes, content_type: str = "application/json") -> tuple[int, dict[str, Any]]:
    request = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": content_type})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read())


def test_http_surface(server: str) -> None:
    with urllib.request.urlopen(f"{server}/health", timeout=10) as response:
        assert json.loads(response.read())["status"] == "ok"
    body = json.dumps({"kind": "prd", "files": [{"path": "p.md", "text": "# PRD\n\nA product requirement line."}]})
    status, payload = _post(f"{server}/evaluate", body.encode())
    assert status == 200 and payload["kind"] == "prd" and 0 <= payload["score"] <= 1
    assert _post(f"{server}/evaluate", b"{}")[0] == 400
    assert _post(f"{server}/evaluate", b"not json")[0] == 400
    assert _post(f"{server}/evaluate", b"{}", "text/plain")[0] == 415
    assert _post(f"{server}/other", b"{}")[0] == 404
