---
title: Team Document Evaluation Workspace
description: Proposed architecture and implementation plan for manual Foundry judge scans of DT, BRD, and PRD submissions.
ms.date: 2026-10-20
---

## Status and scope

The user approved the design and implementation on 2026-10-20. Deployment and
resource provisioning are not approved. The initial implementation is present;
live judge evaluation and connected imports remain incomplete. See the
[integration handoff](integration-plan.md) for verified checks and remaining gates.

Extend the existing React 18, TypeScript, and Vite application with an Evaluation
workspace while preserving the existing command-center prototype. Different teams
submit source evidence and Design Thinking (DT), Business Requirements Document
(BRD), and Product Requirements Document (PRD) artifacts to an existing Microsoft
Foundry judge. Scans start only when a member explicitly requests one.

The user approved Entra sign-in, team-scoped roles, separate team thresholds for
DT/BRD/PRD, PDF/DOCX/Markdown/text uploads, versioned scan snapshots, and preserving
the existing prototype. A 0-100 score scale is conditional on the judge contract.
The suggested 20 MB file limit and 90-day retention remain proposed policy values.

## Experience and workflow

1. Sign in and choose a team from authorized memberships.
2. Open a project and create a submission with a name and description.
3. Upload source evidence or import supported document links. Assign each
   document its role: source evidence, DT, BRD, or PRD.
4. Review imported versions and select the evidence and target documents.
5. Review the team's thresholds and the rubric version before selecting Run scan.
6. Track queued, preparing, evaluating, and completed states without holding an
   HTTP request open for the entire evaluation.
7. Review per-document scores, threshold decisions, criterion feedback, evidence
   references, and recommended improvements.
8. Upload revised documents and explicitly run another scan. Compare runs without
   replacing earlier results or silently rescoring history.

Source evidence grounds the assessment; it is not itself a DT/BRD/PRD target.
One scan can select any nonempty subset of target types. Unselected types display
Not scanned and do not imply success. Missing dependencies required by the chosen
rubric prevent evaluation or produce an inconclusive outcome with an explanation.

## Screens

* Team dashboard with recent submissions, scan status, and results by artifact type
* Submission editor with evidence and DT/BRD/PRD sections, upload/import progress,
  validation errors, and immutable version selection
* Scan review with selected inputs, rubric, thresholds, and explicit start action
* Results detail with per-document scorecards, criterion feedback, evidence
  locators, improvement actions, and technical error details safe for users
* History and comparison view for document revisions and previous scans
* Team settings for memberships and versioned DT/BRD/PRD thresholds

Keyboard access, labelled form controls, announced progress/errors, and text labels
in addition to color are acceptance requirements.

## Roles and isolation

* Team administrators manage memberships and acceptance thresholds and can submit
  documents and run scans.
* Members create submissions, import documents, and request scans.
* Reviewers view authorized documents, scan results, and history without mutation.

Entra authentication establishes identity; server-side membership establishes
authorization. Every data operation, blob download, job, and result lookup checks
tenant and team ownership. Never trust a team ID supplied by the browser without
membership validation. Bootstrap of the first administrator is an operator action,
not self-service privilege escalation.

## Proposed architecture

Use the existing React frontend, an ASP.NET Core API, and a separate .NET worker.
The API and worker can share domain contracts without putting credentials or
acceptance logic in the browser. Use Azure App Service for the API and Azure
Container Apps for the worker, Azure SQL Database for relational records, private
Azure Blob Storage for files, and Azure Service Bus for asynchronous jobs.

```text
Browser -- Entra sign-in --> React Evaluation workspace
                                 |
                         Authorized API
                          /     |     \
                 Azure SQL    Blob    Service Bus
                                         |
                                  Evaluation worker
                                   /           \
                           Document import    Foundry judge
                                   \           /
                              Validated result
                                     |
                              Azure SQL + Blob
```

Use managed identities and narrowly scoped roles for Azure dependencies and
Foundry. Access the existing Foundry agent from the worker only. Confirm the agent
API generation before selecting its exact SDK and invocation contract.

Persist a scan and outbox event atomically; publish the event reliably to the
queue. Workers use an idempotent job key and leases, bounded retries, timeouts,
dead-letter handling, and recovery for interrupted jobs. Record remote invocation
IDs to reconcile ambiguous timeouts instead of blindly repeating billable calls.
The browser polls the authorized scan-status endpoint for the MVP.

Production requires dynamic API and worker hosting; the existing GitHub Pages
deployment alone cannot provide authenticated uploads or execute Foundry scans.
No Azure resources are provisioned until deployment scope and costs are approved.

## Source ingestion

Uploads support PDF, DOCX, Markdown, and plain text. Validate file signatures and
size, quarantine files for malware scanning, and bound extracted text, archive
expansion, document count, and total submission size. Reject unsupported,
encrypted, unreadable, or empty documents explicitly. Scanned PDFs requiring OCR
are outside the initial implementation unless separately approved.

Support SharePoint/OneDrive through a Microsoft Graph connector with explicitly
approved access and consent. Do not forward arbitrary browser credentials or give
the worker unrestricted access to the tenant. Support Azure Blob links through
authorized storage access. Avoid retaining SAS tokens in database URLs or logs.
Resolve imports server-side and preserve content hashes and provider version IDs.

Public HTTPS ingestion requires an operator-configured host allowlist, validated
DNS resolution and redirects, private/link-local/metadata IP blocking, restricted
egress, content/size limits, and bounded timeouts. Do not expose a generic fetch
proxy. An inaccessible link is an import error, never an empty successful input.

The user also selected Other link sources; these remain unsupported until specific
providers and authorization requirements are identified.

At scan start, pin immutable imported/uploaded versions. Changed remote content is
captured by an explicit refresh/import and a new scan, not by changing an old run.
Store safe source attribution without secret query parameters. Documents and their
embedded instructions are untrusted evidence, not authority to change the rubric,
tool permissions, or acceptance policy.

## Scoring and acceptance

Administrators configure an independent numeric minimum for DT, BRD, and PRD.
Validate finite values within the confirmed scoring scale. Threshold edits create
a new policy version, with actor and timestamp; scans reference the captured
version. An administrator must configure all three thresholds before the first
scan. Do not invent organization-approved minimums.

The judge returns a validated structured assessment containing target document
version, document type, rubric version, overall score, criterion scores, rationale,
evidence locators, and improvement actions. This is the application's required
internal contract, not an assertion about the existing agent's response schema.
Build an explicit adapter after the actual response is known.

Validate expected target coverage, unique document identities, scoring scale,
required criteria, finite numeric values, and resolvable evidence references. If a
rubric defines weights, verify weight totals and aggregation rules. Preserve raw
responses privately for diagnostic retention, not as trusted HTML in the UI.

The server determines acceptance, not the judge's free-text verdict:

| Condition                                | Document decision                      |
|------------------------------------------|----------------------------------------|
| Valid score greater than minimum         | Pass                                   |
| Valid score equal to minimum             | Pass                                   |
| Valid score below minimum                | Fail                                   |
| Missing or invalid required assessment   | Inconclusive                           |
| Target type not selected                 | Not scanned                            |
| Import, extraction, or invocation failure | Technical failure; no fabricated score |

A scan is accepted only if every selected target passes. Any technical or invalid
assessment makes the aggregate outcome Inconclusive, with valid partial results
still visible. Otherwise any failed target makes it Fail. Keep job execution
status separate from acceptance outcome: a completed assessment can fail its
threshold without being a technical failure.

Criterion-level minimums and custom rubric authoring are future extensions, not
implicit MVP settings. Do not normalize a score unless the original scale and
conversion are explicit and recorded.

## Data model

* Team and Membership: tenant, identity, role, and membership audit metadata
* Project and Submission: team ownership and user-visible submission metadata
* Document and DocumentVersion: role/type, private blob reference, content hash,
  format, extraction status, safe source attribution, and remote version
* RubricVersion: required criteria, scoring scale, aggregation contract, and
  compatible judge reference
* ThresholdPolicyVersion: team, DT/BRD/PRD minima, actor, and effective timestamp
* Scan and ScanInput: requester, selected immutable versions, evidence mappings,
  captured policy/rubric/judge versions, execution status, and aggregate outcome
* Assessment and CriterionResult: normalized scores, evidence, rationale,
  improvements, and deterministic decision
* OutboxEvent and AuditEvent: reliable dispatch and traceable user/system actions

Use foreign keys and ownership constraints as defense in depth. Store content in
blobs rather than relational rows. Do not place access tokens in scan snapshots.

## API surface

Provide authorized operations to list teams/projects/submissions, create
submissions, upload/import document versions, view import status, request a scan,
retrieve scan progress/results, and list scan history. Restrict membership and
threshold-policy mutations to administrators.

Requesting a scan returns HTTP 202 with an opaque scan identifier after durable
creation. Support an idempotency key to prevent accidental duplicate submissions.
Apply optimistic concurrency to team policy changes. Validate authorization again
when serving downloads and results, not only on creation.

## Known integration prerequisites

The supplied Foundry project endpoint is:

<https://danny-ai-svc.services.ai.azure.com/api/projects/danny-ai-svc-project>

This endpoint identifies the project, not the judge agent.

Before live scans, obtain the agent name or ID, version/API generation, permitted
identity, input contract, scoring scale, rubric, sanitized response example, and
document/context limits. Do not ask for access keys in chat or commit credentials.
If permissions permit, discover agent metadata through authenticated tooling.

Before authenticated application testing, configure Entra tenant/application
registrations, redirect URIs, API audience/scopes, and initial team administrator.
Before connected imports, confirm SharePoint consent/sites and Blob access scopes.

UI and domain/API implementation can proceed after design approval. Live
integration remains explicitly blocked until these prerequisites are resolved;
test fixtures must never appear as actual judge results.

## Operations and retention

Propose a 20 MB per-file limit and 90-day content/result retention, subject to the
organization's data policies. Confirm total submission limits, backup retention,
legal holds, and whether audit metadata requires a different retention period.
Implement a deletion process covering source files, extracted text, diagnostic
responses, results, and any Foundry-side uploaded content or conversations.

Collect correlation IDs, queue age, job duration, sanitized failure codes, and
usage/cost metrics. Do not log document contents, secrets, or signed URLs.
Set upload and scan rate limits plus per-team concurrency and token budgets.
Use configured budgets rather than unbounded retries or evaluations.

## Implementation phases

1. Confirm design approval and inspect the existing judge integration contract.
2. Add domain contracts, migrations, authorization, policy versioning, and
   deterministic acceptance tests.
3. Build the Evaluation workspace using existing React conventions, including
   submission, results, history, and team settings views.
4. Implement private uploads, extraction/quarantine, provider-specific imports,
   ownership checks, and immutable document versions.
5. Implement durable manual scan dispatch, worker recovery, Foundry adapter,
   structured result validation, and explicit technical/inconclusive states.
6. Wire the frontend to live API operations, configure identities and integrations,
   and document setup and outstanding operational prerequisites.
7. Run targeted unit/integration tests, existing frontend checks, and an authorized
   end-to-end live scan. Prepare costed deployment configuration only when approved.

## Verification and release gates

* Verify scores immediately below, equal to, and above each document threshold.
* Reject missing, nonnumeric, nonfinite, duplicate, out-of-range, or wrong-document
  results and missing required criteria without producing a pass.
* Verify threshold edits do not alter running or historical scans.
* Deny cross-team reads, writes, blob access, and forged job ownership.
* Verify role restrictions and Entra token issuer/audience validation.
* Verify file limits, unsafe links/redirects, extraction failures, and failed
  provider authorization produce explicit actionable errors.
* Verify idempotent dispatch, duplicate delivery, retries, worker interruption,
  timeouts, dead letters, and partial results do not fabricate success.
* Verify rescan comparisons retain original document/rubric/policy references.
* Run existing tests, lint, and TypeScript/production build without regressing the
  command-center prototype.
* Perform an authenticated live scan against the configured judge and verify
  persisted results after restart before declaring the integration complete.

## Out of scope

Scheduled scans, change-triggered scans, automatic threshold changes, replacement
judge creation, custom rubric editing, unspecified Other connectors, OCR, and Azure
deployment without separate approval are outside the initial implementation.
