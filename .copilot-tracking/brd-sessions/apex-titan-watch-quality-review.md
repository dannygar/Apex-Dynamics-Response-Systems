# BRD Quality Reviewer — Define-phase run (single invocation)

BRD: apex-titan-watch-brd.md v0.1.0 · Reviewer: BRD Quality Reviewer subagent (read-only) · Date: 2026-10-13

Agent is idle (waiting for messages). agent_id: 99130e3c-db2c-41d8-9b26-cfc1a27c58be, agent_type: general-purpose, status: idle, description: BRD Quality Reviewer, Define phase, elapsed: 274s, total_turns: 1

[Turn 0]
I graded the Titan Watch BRD as **FAIL**, so `define_exit` is **BLOCKED** and `govern_exit` is NOT_EVALUATED. I didn't change any files, and none of the files I read tried to change my behaviour.

### 1. BRD_STANDARD_FINDINGS_V1

```yaml
schema_version: BRD_STANDARD_FINDINGS_V1
assessment_id: requirements-quality-BRD-2026-Q4-TITAN-001-20261013T204000Z
assessed_at: "2026-10-13T20:40:00Z"
brd:
  id: BRD-2026-Q4-TITAN-001
  version: 0.1.0
  phase: Define
  artifact_path: .copilot-tracking/brd-sessions/apex-titan-watch-brd.md
standard:
  skill_name: requirements-quality
  skill_version: "1.1"
mode: plan
assessment_outcome: plan
overall_status: RISK
summary_counts:
  RISK: 3
  CAUTION: 24
  COVERED: 8
  NOT_APPLICABLE: 2
findings:
  - finding_id: req-qual-001
    checklist_item: smart-business-goal-measurable
    requirement_id: BG-001
    status: RISK
    severity: HIGH
    location:
      section: "Business Goals"
      line_range: L92-L179
    finding: "BG-001, BG-002, BG-003, BG-004, BG-005 and BG-007 each name a KPI but record Baseline TBD and/or Target TBD. That fails SMART Measurable. Frontmatter business_goal_smart_status is 'deferred', but the SMART rubric requires every goal to pass at Define exit; deferral is not allowed."
    recommendation: "Run the baseline replay sessions (OQ-005) and record a numeric baseline and target for each failing goal. For example, turn BG-001's '>=50% reduction' proposal into a committed target. Then set business_goal_smart_status to the evaluated result."
  - finding_id: req-qual-002
    checklist_item: smart-business-goal-achievable
    requirement_id: BG-001
    status: RISK
    severity: HIGH
    location:
      section: "Business Goals"
      line_range: L92-L179
    finding: "With no stated targets, achievability for BG-001 to BG-005 and BG-007 cannot be judged against CON-001 (about 12 weeks), CON-002 (one artifact) or CON-004 (limited replay data). The BRD's own SMART block leaves Achievable unchecked."
    recommendation: "Once targets are set, record why each is achievable against CON-001, CON-002 and CON-004, plus the risk if it is a stretch target, or link an open question that holds that rationale."
  - finding_id: req-qual-003
    checklist_item: iso-29148-feasible
    requirement_id: FR-016
    status: RISK
    severity: MEDIUM
    location:
      section: "Functional Requirements"
      line_range: L227
    finding: "FR-016 is a Could for Increment 1, but the BRD says no data source exists (OQ-008 deferred to Future-Release) and CON-004 limits data to the sanitized replay. Known constraints therefore make it infeasible in this time-box (feasible = 0)."
    recommendation: "Move FR-016 to Won't-have-this-time and name the forum where it will be reconsidered, or name a data source and add an acceptance criterion."
  - finding_id: req-qual-004
    checklist_item: iso-29148-unambiguous
    requirement_id: FR-006
    status: CAUTION
    severity: HIGH
    location:
      section: "Functional Requirements"
      line_range: L217-L225
    finding: "The key qualifier 'decision-relevant' in FR-006, FR-011 and FR-014 (and in BG-003 and NFR-005) is not defined in the Glossary, and the BRD doesn't say who designates it. Unambiguous scores 1, below the core minimum of 2."
    recommendation: "Add a Glossary definition of 'decision-relevant' and name the role that designates decision-relevant attributes and changes. A minimum set (for example location, identity, severity, unit commitment) could also be captured as a BR."
  - finding_id: req-qual-005
    checklist_item: iso-29148-unambiguous
    requirement_id: FR-012
    status: CAUTION
    severity: HIGH
    location:
      section: "Functional Requirements"
      line_range: L212-L224
    finding: "Several FRs use vague or undefined terms: 'in one place' (FR-001), 'any item' (FR-002), 'weak' coverage (FR-012) and 'room distance' (FR-013). Each has a dominant reading but no bound, so unambiguous scores 1."
    recommendation: "Define 'item', 'in one place' (for example, without switching systems or roles) and 'room distance'. Give FR-012 a criterion for weak coverage, such as fewer than N active source types per area or silence longer than T minutes."
  - finding_id: req-qual-006
    checklist_item: iso-29148-unambiguous
    requirement_id: NFR-005
    status: CAUTION
    severity: HIGH
    location:
      section: "Non-Functional Requirements / Performance Efficiency"
      line_range: L255
    finding: "NFR-005 requires detection 'within a target time' with Target TBD (OQ-005), so the threshold is unquantified. AC-015 (FR-015) uses the same TBD threshold."
    recommendation: "Close OQ-005 and set a numeric detection time for NFR-005 and AC-015 (for example 'within 60 s of the seeded change'), noting where the baseline came from."
  - finding_id: req-qual-007
    checklist_item: iso-29148-unambiguous
    requirement_id: NFR-002
    status: CAUTION
    severity: HIGH
    location:
      section: "Non-Functional Requirements / Usability"
      line_range: L267-L270
    finding: "NFR-002's 'safety-critical tasks' are not listed, NFR-003's 'remains usable' has no criterion, and NFR-004's 'beyond current practice' assumes a current-practice baseline the BRD doesn't record."
    recommendation: "List the safety-critical tasks, for example by referencing FR IDs. Define 'usable' as task completion without loss of content or function at 200% zoom. Record the current manual update tasks as NFR-004's baseline."
  - finding_id: req-qual-008
    checklist_item: iso-29148-singular
    requirement_id: FR-005
    status: CAUTION
    severity: HIGH
    location:
      section: "Functional Requirements"
      line_range: L213-L225
    finding: "Several FRs bundle behaviours that can be tested separately. FR-002 has four time and status meanings. FR-004 has association source plus persistence of competing tracks. FR-005 has the summary label, traceability to evidence and the confirmer. FR-007 has changes plus unacknowledged state. FR-010 has contribution, reflection and attribution. FR-013 has group and individual contexts. FR-014 has ordered review plus interruption policy. Singular scores 1."
    recommendation: "Split each compound FR into separate FR IDs with their own ACs (for example FR-005a label, FR-005b evidence trace, FR-005c confirmer), or keep the parent and add child IDs."
  - finding_id: req-qual-009
    checklist_item: iso-29148-singular
    requirement_id: CON-003
    status: CAUTION
    severity: HIGH
    location:
      section: "Non-Functional Requirements; Constraints"
      line_range: L270-L304
    finding: "NFR-003 (magnification plus reduced motion), NFR-006 (renders remaining information plus identifies the degraded dependency), NFR-007 (separate retention plus preserved metadata) and CON-003 (replayed data plus no integration, authentication, persistence or messaging) each combine independent conditions."
    recommendation: "Split them into atomic IDs, for example NFR-003 into magnification and reduced-motion items. Split CON-003 into separate data-source, integration, authentication, persistence and messaging constraints so the conflicts in req-qual-015 can be traced."
  - finding_id: req-qual-010
    checklist_item: iso-29148-verifiable
    requirement_id: NFR-001
    status: CAUTION
    severity: HIGH
    location:
      section: "Non-Functional Requirements"
      line_range: L243-L294
    finding: "No NFR except NFR-005 (whose threshold is TBD) names a verification method, artifact or pass condition. NFR-001, 002, 003, 004, 006, 007 and 008 can plausibly be verified, but no method is named, so verifiable scores 1."
    recommendation: "Give each NFR a verification method and pass condition, for example a grayscale/forced-colors walkthrough with all statuses identified, a keyboard-only run of the listed tasks, or a dependency-outage replay that identifies the failed dependency."
  - finding_id: req-qual-011
    checklist_item: iso-29148-verifiable
    requirement_id: FR-017
    status: CAUTION
    severity: HIGH
    location:
      section: "Functional Requirements; Traceability Matrix"
      line_range: L227-L228
    finding: "FR-016 and FR-017 have no acceptance criteria. FR-017 has no stated reason for the gap, so its verification method is only implied (verifiable 1)."
    recommendation: "Add a Given/When/Then AC for FR-017 (for example, replaying the state at decision time T shows only the information available by T), or move it to Won't-this-time. Handle FR-016 as in req-qual-003."
  - finding_id: req-qual-012
    checklist_item: iso-25010-security-coverage
    status: CAUTION
    severity: HIGH
    location:
      section: "Non-Functional Requirements / Security"
      line_range: L280-L284
    finding: "There are no Security NFRs, even though the BRD handles sensor and military data whose classification is open (OQ-003) and an IT security owner is unnamed. The absence is noted but has no owner-approved scoping decision."
    recommendation: "Add Security NFRs for handling sanitized data, demonstration-environment confidentiality and integrity of replay data. Alternatively, record an explicit, owner-approved exclusion tied to CON-003 and OQ-003."
  - finding_id: req-qual-013
    checklist_item: iso-29148-complete
    requirement_id: NFR-005
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Non-Functional Requirements"
      line_range: L243-L294
    finding: "NFR-005's condition is incomplete (missing threshold). No NFR has a MoSCoW priority, and only NFR-005 and NFR-008 link to a business goal, so completeness of the NFR set scores 1."
    recommendation: "Give every NFR a priority, a linked BG and a verification pointer, using the same fields as the FR table."
  - finding_id: req-qual-014
    checklist_item: iso-29148-conforming
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Glossary; Non-Functional Requirements"
      line_range: L472-L484
    finding: "Terms used in requirements are missing from the Glossary: decision-relevant, item, agreed incident picture, safety-critical task, degraded state, stale, room distance. NFRs use a looser statement form than FRs (no actor, trigger or priority)."
    recommendation: "Add the missing terms to the Glossary and put NFRs in a consistent structured form (ID, statement, category, priority, BG, verification, source)."
  - finding_id: req-qual-015
    checklist_item: iso-29148-feasible
    requirement_id: NFR-007
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Non-Functional Requirements; Constraints"
      line_range: L288-L304
    finding: "CON-003 excludes persistence in Increment 1. NFR-007 (raw observations retained for later testing) and FR-017 (review a past decision as it appeared then) imply retained history, and the BRD doesn't reconcile the two (feasible 1)."
    recommendation: "State whether 'no persistence' means no operational data store and whether replay-file retention satisfies NFR-007 and FR-017. Adjust CON-003 or the dependent requirements to match."
  - finding_id: req-qual-016
    checklist_item: iso-29148-correct
    requirement_id: NFR-004
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Non-Functional Requirements / Usability"
      line_range: L270
    finding: "NFR-004 (zero new mandatory manual update tasks for dispatch or field units) conflicts with FR-008 (accepted/moving states), FR-010 (field corrections) and FR-011 (role acknowledgment), which imply new update actions. ASM-005 records the tension as untested."
    recommendation: "Resolve the conflict. Either scope NFR-004 to exclude actions that replace existing radio or board updates, or state which FR inputs are optional, and get confirmation from the dispatch and field representatives."
  - finding_id: req-qual-017
    checklist_item: con-non-negotiability
    requirement_id: CON-002
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Constraints"
      line_range: L303
    finding: "CON-002 says the budget 'may' fund only one artifact, and its non-negotiability is 'Funding not yet approved'. That records uncertainty, not a fixed limit (non-negotiability 1, unambiguous 1)."
    recommendation: "Restate CON-002 as a definite limit with the approving authority and amount or artifact count. If it is still uncertain, move it to Assumptions or Open Questions."
  - finding_id: req-qual-018
    checklist_item: con-separation-from-br-nfr
    requirement_id: CON-006
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Constraints"
      line_range: L306-L307
    finding: "CON-005 (no validated threat-condition rule) and CON-006 (uneven registry reliability) are facts or data-quality risks, not limits imposed by an authority. The listed imposing sources are a person and a data set, and 'uneven' isn't quantified."
    recommendation: "Move CON-005 and CON-006 to Assumptions or the Risk Register, or restate them as imposed limits (for example, 'Increment 1 must not present a computed threat condition as validated', imposed by the data-ops owner)."
  - finding_id: req-qual-019
    checklist_item: con-imposing-source
    requirement_id: CON-007
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Constraints"
      line_range: L308
    finding: "CON-007 is a standing operational policy (cross-jurisdiction approval) rather than a boundary on the solution, and its imposing source ('arrangements') is unnamed pending OQ-004."
    recommendation: "Name the agreement or authority, or move the rule to Business Rules (BR-###) with FR-009 enforcing it."
  - finding_id: req-qual-020
    checklist_item: stakeholder-coverage
    status: CAUTION
    severity: MEDIUM
    location:
      section: "Stakeholders"
      line_range: L56-L77
    finding: "All stakeholder input is simulated. The affected public has no direct voice, steering-group criteria come only through the sponsor (OQ-006), and the legal/compliance and IT security owners are unnamed (OQ-002, OQ-003)."
    recommendation: "Before Govern, confirm the requirements with the real role representatives. Interview one steering member and do targeted community research. Name the legal and security owners."
  - finding_id: req-qual-021
    checklist_item: istqb-testability
    requirement_id: AC-013
    status: CAUTION
    severity: LOW
    location:
      section: "Acceptance Criteria"
      line_range: L357-L359
    finding: "AC-013's pass condition 'completed successfully' is undefined, and AC-015's threshold is TBD. FR-013 and FR-015 can be verified only up to score 2."
    recommendation: "Define success for AC-013 (for example, a correct posture statement within N seconds at a stated distance) and set AC-015's time."
  - finding_id: req-qual-022
    checklist_item: iso-29148-correct
    status: CAUTION
    severity: LOW
    location:
      section: "Frontmatter; Executive Summary; Open Questions"
      line_range: L1-L44
    finding: "The frontmatter dates the BRD 2026-10-13, but the text puts the June 2030 review about 12 working weeks away and sets open-question target dates in March 2030. The document's reference date is inconsistent."
    recommendation: "Align created_date and last_updated with the scenario timeline, or note that the in-scenario date differs from the authoring date."
  - finding_id: req-qual-023
    checklist_item: con-separation-from-br-nfr
    requirement_id: CON-008
    status: CAUTION
    severity: LOW
    location:
      section: "Constraints"
      line_range: L309
    finding: "CON-008 describes the operating context (radio traffic, surge, fatigue, handoffs) rather than an imposed boundary. It works better as test conditions for NFRs."
    recommendation: "Move it to Business Context or make it the stated test conditions for NFR-005 and the ACs."
  - finding_id: req-qual-024
    checklist_item: traceability-br-fr
    requirement_id: BR-006
    status: CAUTION
    severity: LOW
    location:
      section: "Traceability Matrix / BR-to-FR Enforcement"
      line_range: L413-L423
    finding: "BR-006 has no enforcing FR (left to OQ-009 governance). BR-003 (radio and paper fallback stay viable) is mapped to FR-015 (staleness detection), which doesn't enforce the fallback; NFR-008 is closer."
    recommendation: "Add an FR or NFR for replay and mock labeling to enforce BR-006, and remap BR-003 to NFR-008 or a dedicated requirement."
  - finding_id: req-qual-025
    checklist_item: fr-ac-coverage
    requirement_id: FR-016
    status: CAUTION
    severity: LOW
    location:
      section: "Traceability Matrix / FR-to-AC Coverage"
      line_range: L365-L387
    finding: "FR-to-AC coverage is 15/17 (88.24%), above the 80.0% threshold. FR-016 and FR-017 are uncovered."
    recommendation: "Close the two gaps as recommended in req-qual-003 and req-qual-011."
  - finding_id: req-qual-026
    checklist_item: iso-25010-functional-suitability-coverage
    status: CAUTION
    severity: LOW
    location:
      section: "Non-Functional Requirements / Functional Suitability"
      line_range: L247-L249
    finding: "There is no Functional Suitability NFR. Correctness of derived values is deferred to BR-005, which states a policy, not a measurable quality."
    recommendation: "Consider an NFR for functional correctness, for example that derived values match their documented definitions in replay."
  - finding_id: req-qual-027
    checklist_item: iso-25010-portability-coverage
    status: CAUTION
    severity: LOW
    location:
      section: "Non-Functional Requirements / Portability"
      line_range: L292-L294
    finding: "Portability is marked 'Not addressed in Increment 1' with no rationale or owner."
    recommendation: "Record the reason for excluding it (for example, a single demonstration environment under CON-003) or add a portability NFR for the room display and workstation contexts."
  - finding_id: req-qual-028
    checklist_item: id-schema-conformance
    status: COVERED
    severity: N/A
    location:
      section: "Whole document"
    finding: "All FR, AC, NFR, CON, BR and BG IDs use PREFIX-### form. There are no duplicates or cross-namespace collisions, and the frontmatter prefixes match the defaults."
    recommendation: null
  - finding_id: req-qual-029
    checklist_item: fr-bg-traceability
    status: COVERED
    severity: N/A
    location:
      section: "Traceability Matrix / FR-to-BG Alignment"
      line_range: L389-L411
    finding: "All 17 FRs link to at least one valid BG (100.0%). BG-006 has no FR, and the BRD explains why."
    recommendation: null
  - finding_id: req-qual-030
    checklist_item: smart-business-goal-specific-relevant-time-bound
    status: COVERED
    severity: N/A
    location:
      section: "Business Goals"
      line_range: L92-L169
    finding: "All seven goals name an outcome and population (Specific), link to discovery needs (Relevant) and set a June 2030 steering-review window (Time-bound)."
    recommendation: null
  - finding_id: req-qual-031
    checklist_item: iso-29148-necessary
    status: COVERED
    severity: N/A
    location:
      section: "Functional Requirements; Non-Functional Requirements; Constraints"
    finding: "Every FR, NFR and CON cites a discovery source (D01-D11) and, for FRs, a business goal. Minimum necessary score is 2."
    recommendation: null
  - finding_id: req-qual-032
    checklist_item: iso-29148-appropriate
    status: COVERED
    severity: N/A
    location:
      section: "Functional Requirements"
      line_range: L206-L228
    finding: "The FRs are role-neutral and don't prescribe a solution, as DD-006 requires. Only minor design leakage appears (for example 'renders' and 'map imagery' in NFR-006). Minimum appropriate score is 2."
    recommendation: null
  - finding_id: req-qual-033
    checklist_item: moscow-prioritization
    status: COVERED
    severity: N/A
    location:
      section: "Functional Requirements"
      line_range: L208-L241
    finding: "MoSCoW is tied to the Increment 1 time-box. Must is 9 of 17 (53%, under the roughly 60% inflation limit), and each Won't-have-this-time item names a reconsideration forum."
    recommendation: null
  - finding_id: req-qual-034
    checklist_item: con-imposing-source-boundary-category
    requirement_id: CON-003
    status: COVERED
    severity: N/A
    location:
      section: "Constraints"
      line_range: L302-L305
    finding: "CON-001, CON-003 and CON-004 name an imposing source, affected boundary, reason they are non-negotiable and a category explicitly. CON-003's singularity is handled in req-qual-009."
    recommendation: null
  - finding_id: req-qual-035
    checklist_item: acceptance-criteria-form
    status: COVERED
    severity: N/A
    location:
      section: "Acceptance Criteria"
      line_range: L341-L359
    finding: "All 15 ACs use Given/When/Then, each covers a named FR, and all run on the messy replay variant, which addresses R-005."
    recommendation: null
  - finding_id: req-qual-036
    checklist_item: outcome-hypothesis-provenance
    status: NOT_APPLICABLE
    severity: N/A
    location:
      section: "Business Goals / Outcome Hypothesis Handoff Provenance"
      line_range: L181-L183
    finding: "No OUTCOME_HYPOTHESIS_TO_BRD_HANDOFF_V1 payload was supplied."
    recommendation: null
  - finding_id: req-qual-037
    checklist_item: signoff-and-waivers
    status: NOT_APPLICABLE
    severity: N/A
    location:
      section: "Sign-Off"
      line_range: L486-L506
    finding: "Sign-off and waivers belong to Govern, which is not active in this phase."
    recommendation: null
iso_29148_attributes:
  necessary: 2
  appropriate: 2
  unambiguous: 1
  complete: 1
  singular: 1
  feasible: 0
  verifiable: 1
  correct: 1
  conforming: 1
iso_25010_categories:
  functional_suitability: false
  performance_efficiency: true
  compatibility: true
  usability: true
  reliability: true
  security: false
  maintainability: true
  portability: false
smart_business_goals:
  - goal_id: BG-001
    statement: "Reduce the time incident command needs to reach an agreed picture of an escalating incident."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
  - goal_id: BG-002
    statement: "Reduce response actions taken on conditions that are no longer true."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
  - goal_id: BG-003
    statement: "Preserve operational continuity across shift handoff."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
  - goal_id: BG-004
    statement: "Make evidence quality visible so decisions are not made on false certainty."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
  - goal_id: BG-005
    statement: "Ensure response prioritization reflects consequence and observation coverage rather than signal volume."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
  - goal_id: BG-006
    statement: "Enable the June 2030 steering review to make an informed funding decision on an integration program."
    specific: true
    measurable: true
    achievable: true
    relevant: true
    time_bound: true
    overall: PASS
  - goal_id: BG-007
    statement: "Produce valid workflow learning from Increment 1 with representative users."
    specific: true
    measurable: false
    achievable: false
    relevant: true
    time_bound: true
    overall: FAIL
fr_ac_coverage:
  fr_total: 17
  fr_with_ac: 15
  coverage_pct: 88.24
notes: "iso_29148_attributes report the worst score for each attribute across all FR, NFR and CON items, matching the every-requirement gate rule. BG-006 passes Measurable on a binary decision-recorded indicator with a baseline and target. That indicator measures that a decision was recorded, not that it was informed. Stakeholder input is simulated, and the D01-D11 sources were not opened, so 'correct' reflects internal consistency and citation inspection only. The standard bundle is the consolidated brd-author requirements rubric (requirements-author spec_version 1.1), recorded as requirements-quality for contract compatibility. None of the files reviewed contained instructions that try to change reviewer behaviour."
```

### 2. BRD_QUALITY_REPORT_V1

```yaml
schema_version: BRD_QUALITY_REPORT_V1
report_id: BRD-2026-Q4-TITAN-001-quality-20261013T204000Z
generated_at: "2026-10-13T20:40:00Z"
brd:
  id: BRD-2026-Q4-TITAN-001
  version: 0.1.0
  phase: Define
  artifact_path: .copilot-tracking/brd-sessions/apex-titan-watch-brd.md
overall_status: FAIL
decision_thresholds:
  iso_29148_core_min_score: 2
  fr_to_ac_min_pct: 80.0
  fr_to_bg_target_pct: 100.0
gate_decisions:
  define_exit: BLOCKED
  govern_exit: NOT_EVALUATED
summary_counts:
  RISK: 3
  CAUTION: 24
  COVERED: 8
  NOT_APPLICABLE: 2
severity_breakdown:
  CRITICAL: 0
  HIGH: 11
  MEDIUM: 9
  LOW: 7
standards_assessed:
  - skill_name: requirements-quality
    skill_version: "1.1"
    overall_status: RISK
    findings_count: 37
category_summaries:
  iso_29148:
    average_score: 1.11
    weakest_attribute: feasible
    weakest_attribute_score: 0
  iso_25010:
    covered_categories: 5
    missing_categories:
      - functional_suitability
      - security
      - portability
  smart:
    goals_total: 7
    goals_passing: 1
    pass_rate_pct: 14.29
  fr_ac_coverage:
    fr_total: 17
    fr_with_ac: 15
    coverage_pct: 88.24
    threshold_pct: 80.0
  fr_bg_coverage:
    fr_total: 17
    fr_with_bg: 17
    bg_total: 7
    coverage_pct: 100.0
    target_pct: 100.0
    waiver_required: false
top_findings:
  - finding_id: req-qual-001
    standard: requirements-quality
    severity: HIGH
    status: RISK
    location:
      section: "Business Goals"
      line_range: L92-L179
    finding: "Six of seven business goals have Baseline and/or Target TBD and fail SMART Measurable. SMART status is 'deferred', which Define exit does not allow."
    recommendation: "Measure baselines (OQ-005), commit numeric targets, and re-score SMART."
  - finding_id: req-qual-002
    standard: requirements-quality
    severity: HIGH
    status: RISK
    location:
      section: "Business Goals"
      line_range: L92-L179
    finding: "Achievability can't be judged against CON-001, CON-002 or CON-004 without targets."
    recommendation: "Record why each target is achievable against the named constraints."
  - finding_id: req-qual-003
    standard: requirements-quality
    severity: MEDIUM
    status: RISK
    location:
      section: "Functional Requirements"
      line_range: L227
    finding: "FR-016 is infeasible in Increment 1 because no data source exists (OQ-008, CON-004)."
    recommendation: "Move FR-016 to Won't-have-this-time, or name a data source and add an AC."
  - finding_id: req-qual-004
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Functional Requirements"
      line_range: L217-L225
    finding: "'Decision-relevant' is undefined across FR-006, FR-011, FR-014 and NFR-005 (unambiguous 1)."
    recommendation: "Define it in the Glossary and name the role that designates it."
  - finding_id: req-qual-005
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Functional Requirements"
      line_range: L212-L224
    finding: "Vague, unbounded terms appear in FR-001, FR-002, FR-012 and FR-013."
    recommendation: "Define the terms and add a criterion for weak coverage."
  - finding_id: req-qual-006
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Non-Functional Requirements / Performance Efficiency"
      line_range: L255
    finding: "NFR-005 and AC-015 have a TBD time threshold."
    recommendation: "Close OQ-005 and set numeric targets."
  - finding_id: req-qual-007
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Non-Functional Requirements / Usability"
      line_range: L267-L270
    finding: "The safety-critical tasks, the meaning of 'usable', and the current-practice baseline are undefined in NFR-002, NFR-003 and NFR-004."
    recommendation: "List the tasks, define 'usable', and baseline the current manual update tasks."
  - finding_id: req-qual-008
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Functional Requirements"
      line_range: L213-L225
    finding: "Seven FRs bundle behaviours that can be tested separately (singular 1)."
    recommendation: "Split them into atomic FR IDs, each with its own AC."
  - finding_id: req-qual-009
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Non-Functional Requirements; Constraints"
      line_range: L270-L304
    finding: "NFR-003, NFR-006, NFR-007 and CON-003 combine independent conditions."
    recommendation: "Split them into atomic IDs."
  - finding_id: req-qual-010
    standard: requirements-quality
    severity: HIGH
    status: CAUTION
    location:
      section: "Non-Functional Requirements"
      line_range: L243-L294
    finding: "Seven of eight NFRs name no verification method or pass condition (verifiable 1)."
    recommendation: "Give each NFR a verification method and pass condition."
recommendations:
  - id: REC-1
    priority: P1
    target_section: "Business Goals"
    action: "Run the baseline replay sessions. Commit numeric baselines and targets for BG-001 to BG-005 and BG-007, with achievability rationale against CON-001, CON-002 and CON-004. Replace business_goal_smart_status 'deferred' with the evaluated result."
    related_finding_ids:
      - req-qual-001
      - req-qual-002
  - id: REC-2
    priority: P1
    target_section: "Functional Requirements; Glossary"
    action: "Define decision-relevant, item, in one place, weak coverage, room distance, agreed incident picture, stale and degraded state, and say who designates decision-relevant changes."
    related_finding_ids:
      - req-qual-004
      - req-qual-005
      - req-qual-014
  - id: REC-3
    priority: P1
    target_section: "Non-Functional Requirements"
    action: "Set the NFR-005 and AC-015 thresholds, list the safety-critical tasks, define 'usable', and baseline current manual tasks. Give every NFR a verification method, pass condition, priority and linked BG."
    related_finding_ids:
      - req-qual-006
      - req-qual-007
      - req-qual-010
      - req-qual-013
  - id: REC-4
    priority: P1
    target_section: "Functional Requirements; Non-Functional Requirements; Constraints"
    action: "Split compound statements (FR-002, 004, 005, 007, 010, 013, 014; NFR-003, 006, 007; CON-003) into atomic IDs and update the AC and traceability tables."
    related_finding_ids:
      - req-qual-008
      - req-qual-009
  - id: REC-5
    priority: P1
    target_section: "Non-Functional Requirements / Security"
    action: "Add Security NFRs for sanitized-data handling and the confidentiality and integrity of the demonstration environment, or record an owner-approved exclusion tied to OQ-003."
    related_finding_ids:
      - req-qual-012
  - id: REC-6
    priority: P1
    target_section: "Functional Requirements; Constraints"
    action: "Move FR-016 to Won't-have-this-time. Add an AC for FR-017 or move it out of scope. Reconcile CON-003's 'no persistence' with NFR-007 and FR-017."
    related_finding_ids:
      - req-qual-003
      - req-qual-011
      - req-qual-015
      - req-qual-025
  - id: REC-7
    priority: P2
    target_section: "Constraints"
    action: "Restate CON-002 as a definite limit. Move CON-005 and CON-006 to Assumptions or Risks, CON-007 to Business Rules (naming the authority), and CON-008 to Business Context or test conditions."
    related_finding_ids:
      - req-qual-017
      - req-qual-018
      - req-qual-019
      - req-qual-023
  - id: REC-8
    priority: P2
    target_section: "Non-Functional Requirements; Stakeholders"
    action: "Resolve the conflict between NFR-004 and FR-008, FR-010 and FR-011 with the dispatch and field representatives. Replace simulated input with confirmation from real stakeholders, interview a steering member, and name the legal and security owners."
    related_finding_ids:
      - req-qual-016
      - req-qual-020
  - id: REC-9
    priority: P3
    target_section: "Acceptance Criteria; Traceability Matrix; Frontmatter; Non-Functional Requirements"
    action: "Define success for AC-013, add an enforcing requirement for BR-006, remap BR-003, align document dates with the scenario timeline, and record the rationale for missing Functional Suitability and Portability NFRs."
    related_finding_ids:
      - req-qual-021
      - req-qual-022
      - req-qual-024
      - req-qual-026
      - req-qual-027
warnings:
  - id: WARN-001
    severity: ADVISORY
    message: "Stakeholder answers are simulated from discovery documents. Correctness reflects citation inspection only, and none of the requirements are confirmed by stakeholders."
    related_finding_ids:
      - req-qual-020
  - id: WARN-002
    severity: INFO
    message: "ISO 29148 attribute values are the worst score per attribute across all FR, NFR and CON items. Core attributes unambiguous, singular and verifiable each score 1, which breaches the minimum of 2 and blocks on its own, separate from the RISK findings."
  - id: WARN-003
    severity: INFO
    message: "The source documents D01-D11 were not opened, so the review does not verify that the BRD represents them faithfully. None of the files read contained instructions trying to change reviewer behaviour."
notes: "FAIL is triggered by three RISK findings (SMART Measurable and Achievable failures, and FR-016 infeasibility) and by core ISO 29148 attributes below 2. FR-to-AC coverage of 88.24% meets the 80.0% threshold. FR-to-BG coverage is 100%, so no waiver is needed."
```

### 3. Summary

**Overall:** FAIL. **`define_exit`:** BLOCKED. **`govern_exit`:** NOT_EVALUATED.

**Counts by status:** 3 RISK, 24 CAUTION, 8 COVERED, 2 NOT_APPLICABLE (37 findings).

**Counts by severity:** 0 CRITICAL, 11 HIGH, 9 MEDIUM, 7 LOW.

**Top 5 findings:**
1. Six of the seven business goals have no baseline or target yet, so they fail SMART Measurable. The BRD marks SMART as "deferred", which Define exit doesn't allow.
2. The same six goals fail Achievable because there are no targets to check against the constraints.
3. FR-016 can't be built in Increment 1 because there is no data source for it; it should move to Won't-have-this-time.
4. "Decision-relevant" isn't defined anywhere, though FR-006, FR-011, FR-014 and NFR-005 depend on it.
5. Seven functional requirements each combine several separately testable behaviours. Seven of eight non-functional requirements name no way to verify them, and there are no Security NFRs.

The coverage numbers are fine: 88.24% of FRs have acceptance criteria (threshold 80%), and every FR links to a business goal.
