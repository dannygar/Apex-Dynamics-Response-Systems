# BRD Builder — Simulated Session Log (Discover + Define)

Legend: **B** = BRD Builder (per `brd-builder.agent.md`); `[SIMULATED TEAM]` answers come from the DT synthesis (`.copilot-tracking/dt/apex-titan-watch/`) and docs D01–D11.

## Startup

* `[action]` Displays **BRD Requirements Planning CAUTION disclaimer** verbatim; sets `disclaimerShownAt`.
* `[action]` Loads `requirements-author/SKILL.md#discover` → `phaseSkillsLoaded += brd-author#discover`.
* Deviation logged: agent default BRD path is `docs/project-planning/<name>-brd.md` (tracked). Facilitator constraint → BRD written to `.copilot-tracking/brd-sessions/apex-titan-watch-brd.md` (DD-001).

## Turn 1 — Initial questions (agent asks these before creating files)

### 1. 🎯 Business Initiative Context
* 1.a. [x] ✅ Initiative name: Project Titan Watch — shared incident understanding for regional kaiju response (D10 subject line)
* 1.b. [x] ✅ Business problem: roles cannot quickly build and keep a trustworthy shared understanding; gaps hide low-signal communities (DT problem-definition.md)
* 1.c. [x] ✅ Business driver: life-safety outcomes + June 2030 steering funding decision (D01, D09)

### 2. 📋 Scope Boundaries
* 2.a. [x] ✅ Initiative type: process improvement + system capability (Frans: "Both, probably", D01 03:37)
* 2.b. [x] ✅ Primary stakeholders: sponsor Bontle; command, dispatch, data ops, field reps

→ Builder: context sufficient → **creates BRD + state file** (Example Interaction Flow "Clear context").

## Turn 2 — 👥 Stakeholders (3–5 questions per turn)

* 3.a. [x] ✅ Decision owner / sponsor: Bontle Mokgatle (D09, D10 approval)
* 3.b. [x] ✅ Who signs off product completeness? Bratislav Tomic, PO (D10)
* 3.c. [x] ✅ Technical and quality approvers: Snezhana Bozhilova, Reni Borici (D10, D11)
* 3.d. [ ] ❓ Legal/compliance approver? — **not named anywhere in corpus** → Builder asks directly (agent rule: "when a sign-off authority is implied but not named, ask for it directly rather than proceeding") → `[SIMULATED TEAM]` "Unknown" → OQ-002.
* 3.e. [x] ✅ (New) Who represents communities and access needs? Ana Jaksic, Emilia Faria — Builder flags both as low-power/high-interest with high consequence → recommends elevated engagement.

## Turn 3 — 🎯 Business goals / 📊 metrics

* 4.a. [x] ✅ Outcomes: less reconstruction time (D03), fewer stale assignments (D06), handoff continuity (D04), visible evidence quality (D05), consequence-based priority (D07), steering decision (D09), valid learning (D10)
* 4.b. [ ] ❓ Baselines? → `[SIMULATED TEAM]` none measured; only anecdote "first ten minutes" (D01 02:29). Builder records **TBD** rather than inventing (DD-005); flags SMART risk.
* 4.c. [ ] ❓ Measurement source? → replay workflow sessions (D01 04:09 Sabien's test design; D11 messy variants)

## Turn 4 — 🔄 Processes / ⚡ priority

* 5.a. [x] ✅ Current-state flows: sensors → network → desks → analyst → command; field → branch → command → chat (D01, D06)
* 5.b. [x] ✅ Time-box for MoSCoW: Increment 1 through June 2030 steering review (D10)
* 5.c. [x] ✅ Explicit exclusions: automated dispatch (D03, D06), live integrations (D10), Clipzilla (D09–D11)
* 5.d. [x] ✅ (New) Is Clipzilla a requirement? → no BG supports it; FR-to-BG must be 100% → **Won't-this-time** (DD-002)

## Discover hard gate (recorded)

| Criterion | Result |
|---|---|
| Scope bounded | ✅ In/out lists + Won't list |
| Stakeholder ownership explicit | ⚠️ Sponsor/PO/TL/QL named; legal/compliance unnamed → OQ-002 (gate passed with recorded open item) |
| Assumptions & constraints documented | ✅ ASM-001..008, CON-001..008 |
| Seed requirements + traceability scaffold consistent | ✅ |
| Outcome-hypothesis handoff | N/A — none supplied |

**Discover gate: PASSED (conditional on OQ-002).** → `[action]` loads `SKILL.md#define` → `phaseSkillsLoaded += brd-author#define`.

## Define

* Authored FR-001..017, AC-001..015, NFR-001..008, BR-001..007, CON-001..008, DD-001..006, traceability matrices.
* `[action]` Dispatched **BRD Quality Reviewer** once (phase `Define`). Results: `apex-titan-watch-quality-review.md`.
* Reviewer result: **FAIL → `define_exit: BLOCKED`**, `govern_exit: NOT_EVALUATED`. 3 RISK / 24 CAUTION / 8 COVERED / 2 N/A. Top: SMART Measurable + Achievable fail on 6 of 7 BGs (TBD baselines; "deferred" not allowed at Define exit); FR-016 infeasible in Increment 1; "decision-relevant" undefined; 7 non-singular FRs; NFRs lack verification methods; no Security NFRs. FR→AC 88.2%, FR→BG 100%.
* Per agent rule "Define does not exit until the quality report's gate decision permits advancement" — Define remains open; no second reviewer run (facilitator: run once).
* Govern: **skipped** per facilitator directive (no real approvers). Version stays `0.1.0`, status `draft`.
