---
title: agent-eval - Evaluator for DT Coach, BRD Builder, and PRD Builder outputs
description: Coach guide for scoring workshop teams' Design Thinking, BRD, and PRD artifacts with structural checks, case reference coverage, and an Azure OpenAI judge
author: Workshop coach
ms.date: 2026-10-14
ms.topic: how-to
keywords: [evaluation, design thinking, brd, prd, llm-as-judge, hve-core]
---

## What it does

`agent-eval` scores the artifacts that the HVE Core **DT Coach**, **BRD Builder**, and
**PRD Builder** agents write, in three layers:

| Layer | Weight | Checks | Cost |
|---|---|---|---|
| Structure and traceability | 40% | Required files and sections, ID hygiene, FR-to-AC and FR-to-goal coverage, MoSCoW balance, testable acceptance criteria, measurable goals, vague wording, solution leakage, evidence citations, HMW form | Free, instant |
| Reference coverage | 30% | Case-specific expectations in `references/<case>.yaml`: expected insights, stakeholders, Must-haves, Won't-haves, and ideas that must not become Must | Free, instant |
| AI judge | 30% | Rubric criteria code cannot check (insight depth, reframing, atomicity, scope discipline). Every score must quote the artifact; quotes are verified and unverifiable scores are down-weighted | About USD 0.10-0.25 per team (estimate) |

Layers without results are skipped and the remaining weights are renormalized.
Scores support coaching conversations; they do not replace reading the artifacts.

## Setup

The project lives in `evals/agent-eval/` at the repository root and is tracked normally. The
agents' working outputs stay in `.copilot-tracking/`, which the root `.gitignore` excludes; the
evaluator reads them from there.

```powershell
cd evals\agent-eval
uv sync                       # see the package index note below
Copy-Item judge.yaml judge.local.yaml   # then set your endpoint in judge.local.yaml
az login                      # judge uses Entra ID; no API keys are stored
uv run agent-eval judge-check # one tiny call to confirm the judge deployment
```

[judge.yaml](judge.yaml) is a committed template. Put your real endpoint and deployment in
`judge.local.yaml`, which is gitignored and read first. `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_DEPLOYMENT`, and `AZURE_OPENAI_API_VERSION` environment variables override both.

`pyproject.toml` pins a Microsoft-internal package feed as the default index because public PyPI
downloads are blocked on the authoring network. Outside that network, remove the
`[[tool.uv.index]]` block and run `uv lock` before `uv sync`.

### Judge model

| Setting | Value |
|---|---|
| Deployment | `gpt-6.1-sol-judge` (model `gpt-6.1-sol` 2026-09-29, GlobalStandard, 100K TPM) |
| Resource | Any Azure OpenAI or Azure AI Foundry resource in a region that offers `gpt-6.1-sol`; set it in `judge.local.yaml` |
| Auth | Entra ID; your account needs a data-plane role such as Foundry User or Cognitive Services OpenAI User |

The template in [judge.yaml](judge.yaml) includes the `az` command that creates the deployment.

Why this model: it is in the frontier reasoning tier, it is a different model family from the
Claude models that write the artifacts (avoiding self-preference bias), and it is priced in the
mid tier. Re-run the [calibration](#calibration) after changing the judge model.

## Workshop workflow

1. Have each team run the agents into its own slug, for example
   `.copilot-tracking/dt/team-a/` and `.copilot-tracking/brd-sessions/team-a-brd.md`.
2. Score each team:

   ```powershell
   uv run agent-eval run --label team-a `
     --dt ..\..\.copilot-tracking\dt\team-a --brd ..\..\.copilot-tracking\brd-sessions\team-a-brd.md `
     --reference references\apex-titan-watch.yaml --judge --judge-samples 3
   ```

   Add `--prd <file>` once a PRD exists and `--dt-methods 1-3` to limit which DT methods are expected.
3. Compare teams (or models) side by side:

   ```powershell
   uv run agent-eval compare results\team-a-*.json results\team-b-*.json --out results\teams.md
   ```

   With exactly two runs the report adds a delta column and lists regressions.
   `--fail-on-regression` returns exit code 1, which is useful when comparing agent models or settings.

4. Give teams a self-check they can run in Copilot Chat without Azure access:

   ```powershell
   uv run agent-eval prompt brd ..\..\.copilot-tracking\brd-sessions\team-a-brd.md --out results\selfcheck-team-a.md
   ```

   Paste the generated prompt into Copilot Chat; it attaches the files with `#file:` paths relative
   to the repository root and applies the same rubric.

Each run writes `results/<label>-<timestamp>.md` (readable report with "What to improve") and
`.json` (full data for comparisons).

## Reading the results

* ✅ at or above 80%, ⚠️ 50-79%, ❌ below 50%. An artifact cannot show ✅ while any check fails.
* `brd.goal_measurability` fails when baselines or targets are `TBD`. The judge's `goal_quality`
  can still score well when the gap has an owned measurement plan. Treat that pairing as honest,
  and do not let teams invent numbers to pass.
* `judge.*.evidence_verified` reports how many judge quotes were found in the artifact. A low ratio
  means the judge was guessing; its criteria are already down-weighted.
* An instruction-like text warning appears only when the judge quotes the text and the quote is
  found in the artifact. Discussions about AI or prompt injection as a topic are not flagged.
* `--judge-samples 3` keeps the median; a spread of two or more points is flagged as unstable.

## Calibration

[calibration/weak-brd.md](calibration/weak-brd.md) is a deliberately poor BRD with a planted
prompt injection. A healthy judge scores it near zero and reports the injection:

```powershell
uv run agent-eval run --label calibration --brd calibration\weak-brd.md `
  --reference references\apex-titan-watch.yaml --judge --judge-samples 3
```

Baseline results from 2026-10-13 with `gpt-6.1-sol-judge`:

| Input | Overall | Structure | Reference | Judge |
|---|---|---|---|---|
| Dry-run DT (Claude Opus 5.5) | 98% | 99% | 100% | 96% |
| Dry-run BRD (Claude Opus 5.5) | 89% | 91% | 100% | 75% |
| Calibration weak BRD | 23% | 52% | 8% | 0% (all criteria 1/5 across 3 samples; injection reported) |

## Tuning

* [references/apex-titan-watch.yaml](references/apex-titan-watch.yaml) holds the case expectations.
  Each item matches when all `all_of` regexes hit the same table row, bullet, or paragraph. After the
  first workshop, review ✘ items in team reports for false negatives and widen patterns. Record the
  reviewer and date in the file header.
* [src/agent_eval/data/profiles.yaml](src/agent_eval/data/profiles.yaml) holds template expectations
  (sections, DT files, vague words, solution words, Must-share limit).
* [src/agent_eval/data/rubrics.yaml](src/agent_eval/data/rubrics.yaml) holds judge criteria and 1/3/5
  anchors. Pass `--rubrics <file>` to try a variant without editing the default.
* Layer weights are in `DEFAULT_LAYER_WEIGHTS` in `src/agent_eval/models.py`.

## Development

```powershell
uv run pytest       # 65 tests, judge mocked; one test also scores the real dry-run artifacts
uv run ruff check src tests
```

## Limitations

* Regex reference matching can miss unusual phrasings; read ✘ items before docking a team.
* The judge sees at most `max_chars` (200,000) characters per artifact; DT session transcripts are
  excluded by design.
* Cost figures are estimates; confirm current Azure OpenAI pricing for `gpt-6.1-sol`.
* The evaluator measures artifact quality, not facilitation quality or whether the team learned.
