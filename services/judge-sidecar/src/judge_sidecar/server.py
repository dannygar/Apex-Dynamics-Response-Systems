#!/usr/bin/env python3
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Loopback HTTP sidecar that scores one DT, BRD, or PRD artifact per request with agent-eval.

The evaluation worker calls this process on localhost inside the same container
group. agent-eval runs unchanged: structural checks, case-reference coverage,
and the Azure OpenAI judge, combined with agent-eval's layer weights.

Endpoints:
    GET  /health    Liveness plus the active contract version (no model call)
    GET  /contract  Evaluator identity, scale, and layer weights for scan snapshots
    POST /evaluate  {"kind": "dt|brd|prd", "files": [{"path": "...", "text": "..."}]}

Usage:
    judge-sidecar --host 127.0.0.1 --port 8081

Configuration (environment):
    AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT, AZURE_OPENAI_API_VERSION
    JUDGE_SAMPLES (default 1), AGENT_EVAL_REFERENCE (case reference YAML path)
    Authentication uses Microsoft Entra ID (DefaultAzureCredential); API keys are refused.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import logging
import os
import sys
import tempfile
import uuid
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from importlib import resources
from pathlib import Path, PurePosixPath
from typing import Any

import agent_eval
from agent_eval import config
from agent_eval.artifacts import DtBundle, RequirementsDoc
from agent_eval.checks_dt import run_dt_checks
from agent_eval.checks_requirements import run_requirements_checks
from agent_eval.config import ConfigError
from agent_eval.judge import ENV_API_KEY, JudgeSettings, create_client, run_judge
from agent_eval.models import DEFAULT_LAYER_WEIGHTS, ArtifactResult
from agent_eval.reference import dt_reference_checks, requirements_reference_checks

from judge_sidecar import __version__

EXIT_SUCCESS = 0
EXIT_FAILURE = 1
EXIT_ERROR = 2
EXIT_INTERRUPT = 130

ENV_SAMPLES = "JUDGE_SAMPLES"
ENV_REFERENCE = "AGENT_EVAL_REFERENCE"
KINDS = ("dt", "brd", "prd")
MAX_BODY_BYTES = 32 * 1024 * 1024
MAX_DT_FILES = 300
MAX_TOTAL_CHARS = 5_000_000
MAX_PATH_CHARS = 260

logger = logging.getLogger("judge_sidecar")


class InputError(ValueError):
    """Raised when a request is malformed; maps to HTTP 400."""


class EvaluationFailed(RuntimeError):
    """Raised when the judge cannot produce a usable assessment; maps to HTTP 502."""


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()[:12]


def _method_globs() -> list[str]:
    return [spec["folder"] for spec in config.profiles()["dt"]["methods"].values()]


def validate_files(kind: str, files: Any) -> list[tuple[PurePosixPath, str]]:
    """Validate and normalize uploaded artifact files.

    DT bundles keep their relative folder layout (``method-0N-*/<file>.md``). When every
    file shares one leading folder that is not itself a method folder (a browser folder
    upload), that folder is removed so method folders sit at the bundle root.

    Args:
        kind: ``dt``, ``brd``, or ``prd``.
        files: Request ``files`` value.

    Returns:
        Normalized relative paths paired with text.

    Raises:
        InputError: If any path, text, or count is invalid.
    """
    if kind not in KINDS:
        raise InputError("kind must be dt, brd, or prd")
    limit = MAX_DT_FILES if kind == "dt" else 1
    if not isinstance(files, list) or not 1 <= len(files) <= limit:
        raise InputError(f"{kind} requires between 1 and {limit} file(s)")
    result: list[tuple[PurePosixPath, str]] = []
    seen: set[str] = set()
    total = 0
    for item in files:
        if not isinstance(item, dict) or not isinstance(item.get("path"), str) or not isinstance(item.get("text"), str):
            raise InputError("each file needs string path and text")
        raw, text = item["path"].replace("\\", "/"), item["text"]
        path = PurePosixPath(raw)
        if (not raw or len(raw) > MAX_PATH_CHARS or path.is_absolute() or ":" in raw
                or any(part in ("", ".", "..") for part in raw.split("/"))):
            raise InputError("file paths must be relative without '.', '..', drive, or empty segments")
        if kind == "dt" and path.suffix.lower() != ".md":
            raise InputError("DT bundles accept Markdown (.md) files only")
        if "\x00" in text:
            raise InputError("file text must not contain NUL characters")
        # agent-eval reads files with universal newlines; match that so chunking is identical.
        text = text.replace("\r\n", "\n").replace("\r", "\n")
        key = path.as_posix().lower()
        if key in seen:
            raise InputError("duplicate file path")
        seen.add(key)
        total += len(text)
        result.append((path, text))
    if total > MAX_TOTAL_CHARS:
        raise InputError("artifact text exceeds the size limit")
    if kind == "dt" and all(len(p.parts) >= 2 for p, _ in result):
        first = {p.parts[0] for p, _ in result}
        if len(first) == 1 and not any(fnmatch.fnmatch(next(iter(first)).lower(), g) for g in _method_globs()):
            result = [(PurePosixPath(*p.parts[1:]), t) for p, t in result]
    return result


@dataclass
class Evaluator:
    """agent-eval pipeline bound to one judge configuration and case reference."""

    settings: JudgeSettings
    client: Any
    reference: dict[str, Any]
    reference_sha: str
    rubrics: dict[str, Any]

    @classmethod
    def from_env(cls) -> Evaluator:
        """Build the evaluator from environment configuration.

        Raises:
            ConfigError: If configuration is missing, invalid, or uses an API key.
        """
        if os.environ.get(ENV_API_KEY):
            raise ConfigError(f"{ENV_API_KEY} is not accepted; use managed identity or az login")
        reference_path = Path(os.environ.get(ENV_REFERENCE, ""))
        if not reference_path.is_file():
            raise ConfigError(f"{ENV_REFERENCE} must point to the case reference YAML")
        settings = JudgeSettings.load(None, samples=int(os.environ.get(ENV_SAMPLES, "1")))
        return cls(settings=settings, client=create_client(settings), reference=config.load_yaml(reference_path),
                   reference_sha=_sha(reference_path.read_bytes()), rubrics=config.rubrics())

    def contract(self) -> dict[str, Any]:
        """Return the evaluator identity recorded in every scan snapshot."""
        data = resources.files("agent_eval.data")
        rubrics_sha = _sha(data.joinpath("rubrics.yaml").read_bytes())
        profiles_sha = _sha(data.joinpath("profiles.yaml").read_bytes())
        reference_id = str(self.reference.get("id") or "unnamed")
        s = self.settings
        version = (f"agent-eval/{agent_eval.__version__} rubrics/{rubrics_sha} profiles/{profiles_sha} "
                   f"reference/{reference_id}@{self.reference_sha} judge/{s.deployment}@{s.api_version}x{s.samples}")
        return {
            "version": version,
            "evaluator": f"agent-eval {agent_eval.__version__}",
            "sidecar": __version__,
            "reference": {"id": reference_id, "sha": self.reference_sha},
            "rubrics_sha": rubrics_sha,
            "profiles_sha": profiles_sha,
            "judge": {"deployment": s.deployment, "api_version": s.api_version, "samples": s.samples},
            "scale": {"minimum": 0, "maximum": 100},
            "layer_weights": dict(DEFAULT_LAYER_WEIGHTS),
            "kinds": list(KINDS),
        }

    def evaluate(self, kind: str, files: Any) -> dict[str, Any]:
        """Score one artifact with every agent-eval layer.

        Raises:
            InputError: If the files are invalid.
            EvaluationFailed: If the AI judge layer fails; no partial score is returned.
        """
        validated = validate_files(kind, files)
        profiles = config.profiles()
        artifact = ArtifactResult(kind=kind, source=kind)
        with tempfile.TemporaryDirectory(prefix="agent-eval-") as tmp:
            root = Path(tmp)
            if kind == "dt":
                for rel, text in validated:
                    target = root.joinpath(*rel.parts)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_text(text, encoding="utf-8", newline="\n")
                bundle = DtBundle.load(root, profiles["dt"])
                if not bundle.files:
                    raise InputError("DT bundle has no evaluable Markdown files")
                artifact.checks = run_dt_checks(bundle, profiles, None, self.reference.get("citation_patterns"))
                artifact.checks += dt_reference_checks(bundle, self.reference)
                text = bundle.combined_text()
            else:
                target = root / f"{kind}.md"
                target.write_text(validated[0][1], encoding="utf-8", newline="\n")
                doc = RequirementsDoc.load(kind, target, profiles[kind])
                artifact.checks = run_requirements_checks(doc, profiles)
                artifact.checks += requirements_reference_checks(doc, self.reference)
                text = doc.text
        try:
            outcome = run_judge(kind, text, self.rubrics[kind], self.client, self.settings)
        except Exception as exc:
            # Exception text can echo artifact content or endpoint details; keep only the type.
            logger.error("AI judge failed for %s: %s", kind, type(exc).__name__)
            raise EvaluationFailed("AI judge layer failed; no score produced") from exc
        artifact.checks += outcome.checks
        artifact.finalize(DEFAULT_LAYER_WEIGHTS)
        if "judge" not in artifact.layer_scores:
            raise EvaluationFailed("AI judge layer produced no scored criteria")
        return {
            "invocation_id": str(uuid.uuid4()),
            "contract_version": self.contract()["version"],
            "kind": kind,
            "score": artifact.score,
            "status": artifact.status,
            "layer_scores": artifact.layer_scores,
            "checks": [
                {"id": c.id, "layer": c.layer, "title": c.title, "score": c.score, "status": c.status,
                 "weight": c.weight, "detail": c.detail, "evidence": c.evidence, "guidance": c.guidance}
                for c in artifact.checks
            ],
            "summary": outcome.summary,
            "injection_detected": outcome.injection_detected,
            "truncated": outcome.truncated,
            "usage": outcome.usage,
        }


def make_handler(evaluator: Evaluator) -> type[BaseHTTPRequestHandler]:
    """Create a request handler bound to ``evaluator``."""

    class Handler(BaseHTTPRequestHandler):
        server_version = f"judge-sidecar/{__version__}"

        def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - signature fixed by base class
            logger.debug("%s %s", self.command, self.path)

        def _send(self, status: HTTPStatus, body: dict[str, Any]) -> None:
            payload = json.dumps(body, allow_nan=False, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def do_GET(self) -> None:  # noqa: N802 - http.server naming
            if self.path == "/health":
                self._send(HTTPStatus.OK, {"status": "ok", "contract_version": evaluator.contract()["version"]})
            elif self.path == "/contract":
                self._send(HTTPStatus.OK, evaluator.contract())
            else:
                self._send(HTTPStatus.NOT_FOUND, {"error": "not found"})

        def do_POST(self) -> None:  # noqa: N802 - http.server naming
            if self.path != "/evaluate":
                self._send(HTTPStatus.NOT_FOUND, {"error": "not found"})
                return
            try:
                length = int(self.headers.get("Content-Length") or "0")
            except ValueError:
                length = -1
            if not 0 < length <= MAX_BODY_BYTES:
                self._send(HTTPStatus.REQUEST_ENTITY_TOO_LARGE if length > 0 else HTTPStatus.LENGTH_REQUIRED,
                           {"error": "request body missing or too large"})
                return
            if not (self.headers.get("Content-Type") or "").lower().startswith("application/json"):
                self._send(HTTPStatus.UNSUPPORTED_MEDIA_TYPE, {"error": "application/json required"})
                return
            try:
                body = json.loads(self.rfile.read(length).decode("utf-8"))
                if not isinstance(body, dict):
                    raise InputError("request body must be an object")
                self._send(HTTPStatus.OK, evaluator.evaluate(str(body.get("kind")), body.get("files")))
            except (InputError, UnicodeDecodeError, json.JSONDecodeError) as exc:
                message = str(exc) if isinstance(exc, InputError) else "request body must be UTF-8 JSON"
                self._send(HTTPStatus.BAD_REQUEST, {"error": message})
            except EvaluationFailed as exc:
                self._send(HTTPStatus.BAD_GATEWAY, {"error": str(exc)})
            except Exception as exc:
                logger.error("Evaluation failed unexpectedly: %s", type(exc).__name__)
                self._send(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": "evaluation failed"})

    return Handler


def create_parser() -> argparse.ArgumentParser:
    """Create the CLI argument parser."""
    parser = argparse.ArgumentParser(prog="judge-sidecar", description=__doc__.splitlines()[0])
    parser.add_argument("--host", default="127.0.0.1", help="Bind address (default loopback only)")
    parser.add_argument("--port", type=int, default=8081, help="Bind port")
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable debug logging")
    return parser


def configure_logging(verbose: bool = False) -> None:
    """Configure logging based on verbosity level."""
    logging.basicConfig(level=logging.DEBUG if verbose else logging.INFO, format="%(levelname)s: %(message)s")
    for noisy in ("httpx", "httpcore", "azure", "openai"):
        logging.getLogger(noisy).setLevel(logging.WARNING)


def main(argv: list[str] | None = None) -> int:
    """Main entry point."""
    args = create_parser().parse_args(argv)
    configure_logging(args.verbose)
    try:
        evaluator = Evaluator.from_env()
        server = ThreadingHTTPServer((args.host, args.port), make_handler(evaluator))
    except (ConfigError, ValueError, OSError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return EXIT_ERROR
    logger.info("judge-sidecar listening on %s:%d (%s)", args.host, args.port, evaluator.contract()["version"])
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nInterrupted by user", file=sys.stderr)
        return EXIT_INTERRUPT
    finally:
        server.server_close()
    return EXIT_SUCCESS


if __name__ == "__main__":
    sys.exit(main())
