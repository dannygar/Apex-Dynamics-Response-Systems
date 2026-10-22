#!/usr/bin/env python3
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Foundry hosted-agent entry point that exposes agent-eval over the Invocations protocol.

Foundry Agent Service runs this container image as a hosted agent and routes
``POST {project}/agents/{name}/endpoint/protocols/invocations`` to ``POST /invocations``.
The payload is the same JSON contract as the sidecar's HTTP API so both deployments
produce byte-compatible results:

    {"action": "contract"}
    {"action": "evaluate", "kind": "dt|brd|prd", "files": [{"path": "...", "text": "..."}]}

The agent authenticates to the judge model with its Foundry agent identity
(DefaultAzureCredential). Configuration uses the same environment variables as the sidecar.

Usage:
    judge-agent            # listens on $PORT or 8088
"""

from __future__ import annotations

import asyncio
import json
import logging
import sys
from typing import Any

from agent_eval.config import ConfigError
from azure.ai.agentserver.invocations import InvocationAgentServerHost
from starlette.requests import Request
from starlette.responses import JSONResponse, Response

from judge_sidecar.server import (
    MAX_BODY_BYTES,
    EvaluationFailed,
    Evaluator,
    InputError,
    configure_logging,
)

EXIT_SUCCESS = 0
EXIT_ERROR = 2

logger = logging.getLogger("judge_agent")


def _json(status: int, body: dict[str, Any]) -> Response:
    return JSONResponse(body, status_code=status, headers={"Cache-Control": "no-store"})


async def handle(evaluator: Evaluator, request: Request) -> Response:
    """Dispatch one invocation to the agent-eval pipeline.

    Args:
        evaluator: Configured agent-eval evaluator.
        request: Incoming Starlette request.

    Returns:
        JSON response: 200 with the result, 400 for invalid input, 502 when the AI judge fails.
    """
    declared = request.headers.get("content-length")
    if declared is not None and (not declared.isdigit() or int(declared) > MAX_BODY_BYTES):
        return _json(413, {"error": "request body too large"})
    raw = await request.body()
    if not raw or len(raw) > MAX_BODY_BYTES:
        return _json(413 if raw else 400, {"error": "request body missing or too large"})
    try:
        body = json.loads(raw.decode("utf-8"))
        if not isinstance(body, dict):
            raise InputError("request body must be an object")
        action = body.get("action", "evaluate")
        if action == "contract":
            return _json(200, evaluator.contract())
        if action != "evaluate":
            raise InputError("action must be contract or evaluate")
        # agent-eval and the model client are synchronous; keep the event loop responsive.
        result = await asyncio.to_thread(evaluator.evaluate, str(body.get("kind")), body.get("files"))
        return _json(200, result)
    except (InputError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        message = str(exc) if isinstance(exc, InputError) else "request body must be UTF-8 JSON"
        return _json(400, {"error": message})
    except EvaluationFailed as exc:
        return _json(502, {"error": str(exc)})
    except Exception as exc:
        # Exception text can include artifact content; log only the type.
        logger.error("Invocation failed unexpectedly: %s", type(exc).__name__)
        return _json(500, {"error": "evaluation failed"})


def create_app(evaluator: Evaluator) -> InvocationAgentServerHost:
    """Create the hosted-agent server bound to ``evaluator``."""
    app = InvocationAgentServerHost()

    @app.invoke_handler
    async def invoke(request: Request) -> Response:
        return await handle(evaluator, request)

    return app


def main() -> int:
    """Main entry point."""
    configure_logging(False)
    try:
        evaluator = Evaluator.from_env()
    except (ConfigError, ValueError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return EXIT_ERROR
    logger.info("judge-agent starting (%s)", evaluator.contract()["version"])
    create_app(evaluator).run()
    return EXIT_SUCCESS


if __name__ == "__main__":
    sys.exit(main())
