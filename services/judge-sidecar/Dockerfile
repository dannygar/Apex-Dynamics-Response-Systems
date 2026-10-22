# Judge image: agent-eval as a Foundry hosted agent (default) or an HTTP judge service. Build context is the repository root.
FROM python:3.11-slim

COPY --from=ghcr.io/astral-sh/uv:0.7.14 /uv /usr/local/bin/uv

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    UV_NO_CACHE=1 \
    AGENT_EVAL_REFERENCE=/app/references/apex-titan-watch.yaml

WORKDIR /app

# Pinned from uv.lock (uv export); public PyPI is used because the lock's internal feed is not reachable from build agents.
COPY services/judge-sidecar/requirements.txt /tmp/requirements.txt
RUN uv pip install --system --index-url https://pypi.org/simple -r /tmp/requirements.txt

COPY evals/agent-eval/pyproject.toml /src/agent-eval/pyproject.toml
COPY evals/agent-eval/src /src/agent-eval/src
COPY evals/agent-eval/references /app/references
COPY services/judge-sidecar/pyproject.toml /src/judge-sidecar/pyproject.toml
COPY services/judge-sidecar/src /src/judge-sidecar/src
RUN uv pip install --system --no-deps --index-url https://pypi.org/simple /src/agent-eval /src/judge-sidecar \
    && useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin judge

USER 10001
# Default entry point is the Foundry hosted agent (Invocations protocol on $PORT, default 8088).
# The internal judge Container App overrides the command with:
#   judge-sidecar --host 0.0.0.0 --port 8081
EXPOSE 8088
CMD ["judge-agent"]
