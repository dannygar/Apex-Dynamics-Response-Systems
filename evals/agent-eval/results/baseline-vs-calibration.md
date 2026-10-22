# Agent output comparison

| Run | Overall | Judge | Created |
|---|---|---|---|
| dry-run | ⚠️ 94% | gpt-6.1-sol-judge | 2026-10-13 21:53 UTC |
| calibration-weak | ❌ 23% | gpt-6.1-sol-judge | 2026-10-13 21:56 UTC |

| Check | dry-run | calibration-weak | Δ |
|---|---|---|---|
| `dt.overall` | 98% | — | — |
| `dt.layer.structure` | 99% | — | — |
| `dt.layer.reference` | 100% | — | — |
| `dt.layer.judge` | 96% | — | — |
| `dt.artifacts` | 100% | — | — |
| `dt.state_file` | 100% | — | — |
| `dt.theme_count` | 100% | — | — |
| `dt.evidence` | 93% | — | — |
| `dt.hmw_count` | 100% | — | — |
| `dt.hmw_neutral` | 100% | — | — |
| `dt.research_honesty` | 100% | — | — |
| `ref.dt.themes` | 100% | — | — |
| `ref.dt.stakeholders` | 100% | — | — |
| `ref.dt.not_themes` | 100% | — | — |
| `judge.dt.problem_reframing` | 100% | — | — |
| `judge.dt.insight_depth` | 100% | — | — |
| `judge.dt.evidence_grounding` | 100% | — | — |
| `judge.dt.stakeholder_breadth` | 100% | — | — |
| `judge.dt.hmw_quality` | 75% | — | — |
| `judge.dt.tension_handling` | 100% | — | — |
| `judge.dt.evidence_verified` | — | — | — |
| `brd.overall` | **89%** | 23% | -66 |
| `brd.layer.structure` | **91%** | 52% | -39 |
| `brd.layer.reference` | **100%** | 8% | -92 |
| `brd.layer.judge` | **75%** | 0% | -75 |
| `brd.frontmatter` | **100%** | **100%** | +0 |
| `brd.sections` | **100%** | **100%** | +0 |
| `brd.placeholders` | **100%** | **100%** | +0 |
| `brd.id_hygiene` | **100%** | **100%** | +0 |
| `brd.fr_ac_coverage` | **100%** | 50% | -50 |
| `brd.fr_goal_coverage` | **100%** | 60% | -40 |
| `brd.goal_measurability` | **14%** | 0% | -14 |
| `brd.nfr_categories` | **69%** | 0% | -69 |
| `brd.requirement_sources` | **100%** | 0% | -100 |
| `brd.acceptance_form` | **100%** | 0% | -100 |
| `brd.moscow` | **100%** | 50% | -50 |
| `brd.vague_terms` | **100%** | 71% | -29 |
| `brd.solution_leakage` | **100%** | 40% | -60 |
| `ref.brd.must_cover` | **100%** | 0% | -100 |
| `ref.brd.expected_wont` | **100%** | 0% | -100 |
| `ref.brd.forbidden_must` | **100%** | 33% | -67 |
| `judge.brd.solution_agnostic` | **50%** | 0% | -50 |
| `judge.brd.goal_quality` | **100%** | 0% | -100 |
| `judge.brd.requirement_atomicity` | **50%** | 0% | -50 |
| `judge.brd.evidence_traceability` | **100%** | 0% | -100 |
| `judge.brd.scope_discipline` | **75%** | 0% | -75 |
| `judge.brd.risk_honesty` | **75%** | 0% | -75 |
| `judge.brd.evidence_verified` | — | — | — |

## Regressions (calibration-weak vs dry-run, tolerance 10%)

* brd.overall: 89% → 23%
* brd.layer.structure: 91% → 52%
* brd.layer.reference: 100% → 8%
* brd.layer.judge: 75% → 0%
* brd.fr_ac_coverage: 100% → 50%
* brd.fr_goal_coverage: 100% → 60%
* brd.goal_measurability: 14% → 0%
* brd.nfr_categories: 69% → 0%
* brd.requirement_sources: 100% → 0%
* brd.acceptance_form: 100% → 0%
* brd.moscow: 100% → 50%
* brd.vague_terms: 100% → 71%
* brd.solution_leakage: 100% → 40%
* ref.brd.must_cover: 100% → 0%
* ref.brd.expected_wont: 100% → 0%
* ref.brd.forbidden_must: 100% → 33%
* judge.brd.solution_agnostic: 50% → 0%
* judge.brd.goal_quality: 100% → 0%
* judge.brd.requirement_atomicity: 50% → 0%
* judge.brd.evidence_traceability: 100% → 0%
* judge.brd.scope_discipline: 75% → 0%
* judge.brd.risk_honesty: 75% → 0%
