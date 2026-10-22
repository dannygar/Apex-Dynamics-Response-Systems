#!/usr/bin/env python3
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Command-line entry point for evaluating DT Coach, BRD Builder, and PRD Builder outputs.

Usage:
    agent-eval run --dt <dt-folder> --brd <brd.md> [--prd <prd.md>] [--reference ref.yaml] [--judge]
    agent-eval compare results/team-a.json results/team-b.json [--out comparison.md]
    agent-eval prompt brd <path-to-brd.md> [--out selfcheck.md]

Example:
    uv run agent-eval run --label team-a --dt ../../.copilot-tracking/dt/team-a \\
        --brd ../../.copilot-tracking/brd-sessions/team-a-brd.md \\
        --reference references/apex-titan-watch.yaml --judge
"""

from __future__ import annotations

import argparse
import logging
import re
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from agent_eval import config
from agent_eval.artifacts import DtBundle, RequirementsDoc
from agent_eval.checks_dt import run_dt_checks
from agent_eval.checks_requirements import run_requirements_checks
from agent_eval.config import ConfigError
from agent_eval.models import ArtifactResult, EvalRun
from agent_eval.reference import dt_reference_checks, requirements_reference_checks
from agent_eval.report import ICON, load_runs, regressions, render_comparison, render_run, write_json

EXIT_SUCCESS = 0
EXIT_FAILURE = 1
EXIT_ERROR = 2
EXIT_INTERRUPT = 130

JUDGE_CONFIG_NAMES = ("judge.local.yaml", "judge.yaml")
PROJECT_ROOT = Path(__file__).resolve().parents[2]

logger = logging.getLogger("agent_eval")


def _parse_methods(value: str | None) -> list[int] | None:
    if not value:
        return None
    numbers: set[int] = set()
    for part in value.split(","):
        if m := re.fullmatch(r"\s*(\d)\s*-\s*(\d)\s*", part):
            numbers.update(range(int(m.group(1)), int(m.group(2)) + 1))
        elif part.strip().isdigit():
            numbers.add(int(part))
        else:
            raise ConfigError(f"Invalid --dt-methods value: {value!r} (use e.g. 1-3 or 1,2,3)")
    return sorted(numbers)


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-") or "run"


def create_parser() -> argparse.ArgumentParser:
    """Create the CLI argument parser."""
    parser = argparse.ArgumentParser(prog="agent-eval", description=__doc__.splitlines()[0])
    parser.add_argument("-v", "--verbose", action="store_true", help="Enable debug logging")
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="Evaluate DT, BRD, and/or PRD outputs")
    run.add_argument("--dt", type=Path, help="DT project folder (.copilot-tracking/dt/<slug>)")
    run.add_argument("--dt-methods", help="DT methods to evaluate, e.g. 1-3 (default: folders present, min 1-3)")
    run.add_argument("--brd", type=Path, help="BRD markdown file")
    run.add_argument("--prd", type=Path, help="PRD markdown file")
    run.add_argument("--reference", type=Path, help="Case-specific reference YAML")
    run.add_argument("--rubrics", type=Path, help="Override judge rubrics YAML")
    run.add_argument("--judge", action="store_true", help="Enable the Azure OpenAI judge layer")
    run.add_argument("--judge-config", type=Path, default=None,
                     help="Judge settings YAML (default: judge.local.yaml, then judge.yaml)")
    run.add_argument("--judge-samples", type=int, default=None, help="Judge calls per artifact (median kept)")
    run.add_argument("--judge-temperature", type=float, default=None,
                     help="Judge temperature (omit for reasoning models that reject it)")
    run.add_argument("--label", default="run", help="Team, model, or configuration label")
    run.add_argument("--out", type=Path, default=Path("results"), help="Output folder for reports")
    run.add_argument("--fail-under", type=float, default=None, help="Exit 1 if overall score is below this (0-1)")

    compare = sub.add_parser("compare", help="Compare saved runs (teams or models)")
    compare.add_argument("runs", nargs="+", type=Path, help="Run JSON files; first is the baseline")
    compare.add_argument("--out", type=Path, help="Write the comparison markdown here")
    compare.add_argument("--tolerance", type=float, default=0.1, help="Regression tolerance (0-1)")
    compare.add_argument("--fail-on-regression", action="store_true",
                         help="Exit 1 if the second run regresses against the first")

    prompt = sub.add_parser("prompt", help="Generate a Copilot Chat self-check prompt for participants")
    prompt.add_argument("kind", choices=["dt", "brd", "prd"])
    prompt.add_argument("paths", nargs="+", help="Workspace-relative file paths or a DT folder to attach")
    prompt.add_argument("--rubrics", type=Path, help="Override judge rubrics YAML")
    prompt.add_argument("--out", type=Path, help="Write the prompt to this file")

    check = sub.add_parser("judge-check", help="Verify the judge deployment with one small call")
    check.add_argument("--judge-config", type=Path, default=None, help="Judge settings YAML")
    return parser


def resolve_judge_config(explicit: Path | None) -> Path | None:
    """Return the judge config file to use, searching default locations."""
    if explicit:
        if not explicit.is_file():
            raise ConfigError(f"Judge config not found: {explicit}")
        return explicit
    for folder in (Path.cwd(), PROJECT_ROOT):
        for name in JUDGE_CONFIG_NAMES:
            if (candidate := folder / name).is_file():
                return candidate
    return None


def configure_logging(verbose: bool = False) -> None:
    """Configure logging based on verbosity level."""
    logging.basicConfig(level=logging.DEBUG if verbose else logging.INFO, format="%(levelname)s: %(message)s")
    if not verbose:
        for noisy in ("httpx", "httpx2", "httpcore", "azure", "openai"):
            logging.getLogger(noisy).setLevel(logging.WARNING)


def _judge_artifact(kind: str, text: str, state: dict[str, Any], artifact: ArtifactResult) -> None:
    from agent_eval.judge import run_judge

    outcome = run_judge(kind, text, state["rubrics"][kind], state["client"], state["settings"])
    artifact.checks += outcome.checks
    for key, value in outcome.usage.items():
        state["usage"][key] = state["usage"].get(key, 0) + value
    if outcome.injection_detected:
        logger.warning("%s: instruction-like text in artifact: %s", kind.upper(), outcome.injection_evidence[:120])


def cmd_run(args: argparse.Namespace) -> int:
    if not (args.dt or args.brd or args.prd):
        raise ConfigError("Provide at least one of --dt, --brd, or --prd")
    profiles = config.profiles()
    reference = config.load_yaml(args.reference) if args.reference else None
    judge_state: dict[str, Any] | None = None
    if args.judge:
        from agent_eval.judge import JudgeSettings, create_client

        config_path = resolve_judge_config(args.judge_config)
        settings = JudgeSettings.load(config_path, args.judge_samples, args.judge_temperature)
        logger.info("Judge: %s @ %s (%d sample(s))", settings.deployment, settings.endpoint, settings.samples)
        judge_state = {"settings": settings, "client": create_client(settings),
                       "rubrics": config.rubrics(args.rubrics), "usage": {}}

    run = EvalRun(label=args.label, created_at=datetime.now(UTC).strftime("%Y-%m-%d %H:%M UTC"),
                  reference_id=(reference or {}).get("id"))

    if args.dt:
        bundle = DtBundle.load(args.dt, profiles["dt"])
        artifact = ArtifactResult(kind="dt", source=str(args.dt))
        artifact.checks = run_dt_checks(bundle, profiles, _parse_methods(args.dt_methods),
                                        (reference or {}).get("citation_patterns"))
        if reference:
            artifact.checks += dt_reference_checks(bundle, reference)
        if judge_state:
            _judge_artifact("dt", bundle.combined_text(), judge_state, artifact)
        run.inputs["dt"] = {"path": str(args.dt), "sha": bundle.fingerprint()}
        artifact.finalize()
        run.artifacts.append(artifact)

    for kind, path in (("brd", args.brd), ("prd", args.prd)):
        if not path:
            continue
        doc = RequirementsDoc.load(kind, path, profiles[kind])
        artifact = ArtifactResult(kind=kind, source=str(path))
        artifact.checks = run_requirements_checks(doc, profiles)
        if reference:
            artifact.checks += requirements_reference_checks(doc, reference)
        if judge_state:
            _judge_artifact(kind, doc.text, judge_state, artifact)
        run.inputs[kind] = {"path": str(path), "sha": doc.fingerprint()}
        artifact.finalize()
        run.artifacts.append(artifact)

    if judge_state:
        run.judge = {**judge_state["settings"].describe(), "usage": judge_state["usage"]}
    run.finalize()

    stem = f"{_slug(args.label)}-{datetime.now(UTC).strftime('%Y%m%d-%H%M%S')}"
    json_path, md_path = args.out / f"{stem}.json", args.out / f"{stem}.md"
    write_json(run, json_path)
    md_path.write_text(render_run(run), encoding="utf-8")

    print(f"{ICON[run.status]} {run.label}: overall {run.score:.0%}")
    for art in run.artifacts:
        layers = ", ".join(f"{k} {v:.0%}" for k, v in art.layer_scores.items())
        failing = sum(1 for c in art.checks if c.status == "fail")
        print(f"  {ICON[art.status]} {art.kind.upper():3} {art.score:.0%}  ({layers}; {failing} failing checks)")
    print(f"Report: {md_path}\nData:   {json_path}")
    if args.fail_under is not None and run.score < args.fail_under:
        return EXIT_FAILURE
    return EXIT_SUCCESS


def cmd_compare(args: argparse.Namespace) -> int:
    runs = load_runs(args.runs)
    text = render_comparison(runs, args.tolerance)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(text, encoding="utf-8")
        print(f"Comparison: {args.out}")
    else:
        print(text)
    if args.fail_on_regression and len(runs) >= 2 and regressions(runs[0], runs[1], args.tolerance):
        return EXIT_FAILURE
    return EXIT_SUCCESS


def cmd_prompt(args: argparse.Namespace) -> int:
    from agent_eval.judge import build_selfcheck_prompt

    paths: list[str] = []
    excluded = set(config.profiles()["dt"]["exclude"])
    for raw in args.paths:
        path = Path(raw)
        if path.is_dir():
            paths += [p.as_posix() for p in sorted(path.rglob("*.md")) if p.name not in excluded]
        else:
            paths.append(path.as_posix())
    paths = [workspace_relative(p) for p in paths]
    text = build_selfcheck_prompt(args.kind, config.rubrics(args.rubrics)[args.kind], paths)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
        print(f"Prompt: {args.out}")
    else:
        print(text)
    return EXIT_SUCCESS


def workspace_relative(path: str) -> str:
    """Return ``path`` relative to the enclosing git repository root for ``#file:`` references.

    Copilot Chat resolves ``#file:`` paths from the workspace root, so a path given relative to
    this project folder would not resolve. Paths outside a repository are returned unchanged.
    """
    resolved = Path(path).resolve()
    for parent in resolved.parents:
        if (parent / ".git").exists():
            return resolved.relative_to(parent).as_posix()
    return Path(path).as_posix()


def cmd_judge_check(args: argparse.Namespace) -> int:
    from agent_eval.judge import JudgeSettings, check_connection, create_client

    config_path = resolve_judge_config(args.judge_config)
    settings = JudgeSettings.load(config_path)
    print(f"Config:     {config_path or 'environment only'}")
    print(f"Endpoint:   {settings.endpoint}\nDeployment: {settings.deployment} (api {settings.api_version}, "
          f"auth {settings.describe()['auth']})")
    result = check_connection(create_client(settings), settings)
    print(f"✅ Judge reachable: model={result['model']}, JSON mode ok, {result['latency_s']:.1f}s, "
          f"{result['total_tokens']} tokens")
    return EXIT_SUCCESS


def main(argv: list[str] | None = None) -> int:
    """Main entry point."""
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    args = create_parser().parse_args(argv)
    configure_logging(args.verbose)
    handlers = {"run": cmd_run, "compare": cmd_compare, "prompt": cmd_prompt, "judge-check": cmd_judge_check}
    try:
        return handlers[args.command](args)
    except (ConfigError, FileNotFoundError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return EXIT_ERROR
    except KeyboardInterrupt:
        print("\nInterrupted by user", file=sys.stderr)
        return EXIT_INTERRUPT
    except BrokenPipeError:
        sys.stderr.close()
        return EXIT_FAILURE
    except Exception as exc:
        logger.debug("Unhandled error", exc_info=True)
        print(f"Error: {exc}", file=sys.stderr)
        return EXIT_FAILURE


if __name__ == "__main__":
    sys.exit(main())
