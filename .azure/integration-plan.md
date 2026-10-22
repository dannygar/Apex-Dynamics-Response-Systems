# Evaluation integration handoff

## Azure deployment (2026-10-21)

- Resource group `rg-apex-eval` (eastus2), deployed by `infra/scripts/Deploy-Evaluation.ps1` from `infra/main.bicep`.
- One Container App per image in a VNet-integrated, consumption-only environment (`azcae…1`, subnet `apps2`):
  web (external API + SPA), worker (no ingress), judge (internal HTTPS, agent-eval contract heartbeat),
  clamav (internal TCP 3310), plus the `azjob…` migration job.
- Scans are scored by the Foundry hosted agent `apex-agent-eval-judge` (Invocations protocol, same judge image)
  in `danny-ai-svc-project`; the worker runs with `Judge__Mode=FoundryAgent`.
- Tenant policy forces SQL and Storage private; both use private endpoints and private DNS. Operator SQL steps
  run as a short-lived job inside the VNet. Subnet `apps` is unused and can be removed later.
- Subscription feature `Microsoft.Network/AllowBringYourOwnPublicIpAddress` was registered for VNet environments.
- From this workstation `az acr build` uploads can hang on a slow uplink; use `-SkipBuild` after building
  through the ACR `scheduleRun` API if that recurs.
- The sections below describe the earlier local sidecar design and remain accurate for local development.

## Platform administration and invitations (2026-10-21)

- Admin team `Apex Workshop - Administrator` (flagged `Teams.IsPlatformAdmin`) is set by the deploy script's
  operator SQL; any member is a platform admin (create/delete teams, manage any membership, read-only access to
  every team, send invitations). Migration `20261022000000_PlatformTeams`.
- Invitations use Entra B2B: the API managed identity holds Graph application permissions User.Invite.All and
  User.ReadBasic.All (granted by `Grant-DirectoryInvitePermissions`; verified from inside the environment).
  `Invitations__RedirectUrl` is the web origin; invitees land on `/?invitation=<id>#evaluation`.
- The SPA has tenant-wide consent (openid, profile, offline_access, email, Evaluation.Access) so guests see no
  consent prompt; API tokens carry the optional `email` claim.
- ClamAV is addressed by app name (`Malware__ClamAvHost=azclam…`): internal TCP ingress is not reachable through
  the internal FQDN.
- Images build through `Invoke-AcrBuild` (one uploaded archive + ACR scheduleRun) instead of `az acr build`.
- Invitation emails (2026-10-21): Graph accepted invites (201, Entra audit "Invite external user" success) but
  @microsoft.com recipients received nothing, consistent with mail filtering of app-sent invitations from tenant
  "Contoso" with no custom message. Invitations are now sent on behalf of the signed-in admin via secret-free OBO
  (`Invitations__DelegatedClientId`; federated credential on the API registration trusting the managed identity,
  verified from inside the environment; delegated User.Invite.All admin-consented). Non-inviter admins fall back to
  the app identity with a note. The console no longer claims delivery and offers an Outlook draft per invitation.
- Self-service (2026-10-21): anyone in the tenant can join any non-admin team as Member (`POST /api/teams/{id}/join`,
  open joining by user decision: any tenant user can read any non-admin team's data by joining) and create teams
  (`POST /api/teams`, creator = that team's Administrator only, 10/hour). Team managers invite via a people picker:
  platform people + directory search that runs only with the caller's delegated `User.ReadBasic.All` (OBO; consented
  tenant-wide by `Register-EntraApps.ps1`) so guest directory restrictions apply; typed emails send B2B invitations.
- Performance (2026-10-22): web keeps one warm replica (`minReplicas: 1`), SQL pool `Min Pool Size=1` plus a
  `DatabaseWarmup` hosted service, Brotli/Gzip response compression, EF/ASP.NET/HttpClient logs at Warning, and the
  command-center map mounts only when first visited. B2B email arrival (minutes) is Microsoft's pipeline.
- Suggested-team edits (2026-10-22): `PATCH /api/admin/invitations/{id}` (platform admins, any pending invitation)
  and `PATCH /api/teams/{team}/invitations/{id}` (team managers, only to teams they manage) change or clear the
  suggested team without sending email; the UI offers an Outlook draft or Resend. Audited `InvitationSuggestionChanged`.
- Deploy CLI profile: a Graph CAE challenge (`TokenCreatedWithOutdatedPolicies`) needs a fresh interactive sign-in;
  `AZURE_CONFIG_DIR=%TEMP%\apex-azcfg2` is a private copy of the refreshed default profile.

## Authority and boundaries

- User explicitly approved implementation in chat on 2026-10-20; approval and
  implementation status are recorded in `project-plan.md`.
- No deployment, provisioning, resource spending, commits or seed data authorized.
- Existing command-center prototype and intentionally unfilled `AGENTS.md` preserved.
- Frontend is already wired to the real API. No mock scores/clients/data exist.
- This artifact does not claim a new workflow session, UI approval or live integration.

## Backend

- Solution: `services/evaluation/Evaluation.sln` (.NET 8).
- API: `services/evaluation/Evaluation.Api`.
- Build: `dotnet build services/evaluation/Evaluation.sln`.
- Initialize: `dotnet run --project services/evaluation/Evaluation.Api -- --initialize-database`.
- Run: `dotnet run --project services/evaluation/Evaluation.Api -- --urls http://localhost:5080`.
- Health: `GET /health` (liveness; returns `judgeReady:false`).
- Worker: `dotnet run --project services/evaluation/Evaluation.Worker`.
- Same DB/storage/scanner configuration for API and worker.
- SDKs installed for this session: `%TEMP%/apex-dotnet/dotnet.exe` (8.0.426 and
  10.0.402). Use a normal operator-managed .NET 8 SDK elsewhere.
- Entra env: `Entra__TenantId`, `Entra__Audience`, `Entra__RequiredScope`
  (default delegated scope name `Evaluation.Access`).
- v2 access tokens: issuer/audience/signature/lifetime and delegated scope checked.
  Every team operation rechecks tenant + membership; worker rechecks requester role.
- Operator bootstraps first team/admin; no development auth bypass or public escalation.

## Frontend

- Folder: repository root (not `services/web`).
- Build/dev: `npm run build` / `npm run dev`.
- Entry: `src/App.tsx`, `#evaluation`; existing command center retained.
- API seam: `src/evaluation/api.ts`; all requests already target live same-origin
  `/api`. Vite dev proxy: `http://localhost:5080`.
- Types: `src/evaluation/contracts.ts`, mirrored from API DTOs and
  `services/evaluation/Evaluation.Core/Domain.cs`. No shared npm alias.
- Identity: `.env.evaluation.example` → `.env.local`; public
  `VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_API_SCOPE`.
- MSAL v5 redirect bridge: `redirect.html`; included as separate Vite build entry.
  Register exact URI, e.g.
  `http://localhost:5173/Apex-Dynamics-Response-Systems/redirect.html`.
  No COOP header on bridge. Register app base for logout too.
- No mock files, mock datasets, local score fixtures, preview switchers or
  duplicate mock clients to delete. Test fixtures remain test-only.
- Authenticated browser/user-approval and cloud E2E validation NOT performed.

## Route inventory

All routes below except health require an Entra delegated bearer token. Prefix
`T = /api/teams/{teamId}`. IDs are GUIDs; enum wire values are strings.

| Method | Route |
| --- | --- |
| GET | `/health` |
| GET | `/api/teams` |
| GET | `T/readiness` |
| GET, POST | `T/projects` |
| GET | `T/projects/{projectId}` |
| GET, POST | `T/projects/{projectId}/submissions` |
| GET | `T/submissions/{submissionId}` |
| POST | `T/submissions/{submissionId}/documents` |
| GET | `T/documents/{versionId}/download` |
| POST | `T/submissions/{submissionId}/imports` (409 prerequisites; never fetches) |
| GET, PUT | `T/policy` (PUT admin; 409 until scoring contract confirmed) |
| GET, PUT | `T/members` (admin) |
| GET, POST | `T/submissions/{submissionId}/scans` (POST blocked until judge ready) |
| GET | `T/scans/{scanId}` |

- Upload multipart fields: `file`, `role` (`Source`, `DT`, `BRD`, `PRD`).
- Scan POST: `{targetVersionIds:[],evidenceVersionIds:[]}` +
  `Idempotency-Key`; durable accepted creation returns 202.
- Full DTO/request descriptions: `services/evaluation/README.md`.

## Persistence, files and queue

- EF migration: `services/evaluation/Evaluation.Core/Migrations/InitialSchema.cs`.
- SQLite local default; `Database__Provider=SqlServer` selects SQL Server.
- Connection: `ConnectionStrings__Evaluation`; absolute local DB path recommended.
- Explicitly **NO SEED DATA**. Apply migration before API/worker startup.
- `Storage__Provider=Local`: `Storage__Root` private directory outside web roots.
- Optional `Storage__Provider=AzureBlob`, `Storage__ContainerUri` existing HTTPS
  private container, no SAS/query. `DefaultAzureCredential`; never creates resources.
- Azure Blob and SQL Server providers compile; no live cloud validation performed.
- `Malware__ClamAvHost`, optional `Malware__ClamAvPort=3310`: missing scanner keeps
  uploads quarantined. Scanner failures reject versions; upload a new version.
- PDF child process: PdfPig, 500 pages / 1M chars / 30s / sampled 256MB. No OCR or
  encrypted PDF. DOCX bounded ZIP/XML; Markdown/text strict UTF-8.
- Durable relational outbox is consumed directly by worker with leases/fencing,
  bounded pre-call retries, dead letters and no repeated ambiguous billable calls.
- Service Bus transport is NOT implemented. Do not describe local outbox as Azure queue.
- Retention/deletion, orphan reconciliation, legal holds, hard parser sandbox,
  distributed quotas and backup/restore remain production release work.

## Essential vs enhancement services

- Essential: React client, Entra, .NET API/worker, relational persistence, private
  file storage, malware scanner, durable dispatch, confirmed existing Foundry judge.
- Cloud topology pending authorized deployment: App Service API, Container Apps
  worker, Azure SQL, private Blob, Service Bus transport.
- Connected import prerequisites + implementation pending: Graph scoped
  SharePoint/OneDrive, authorized Blob links, allowlisted public HTTPS with
  DNS/IP/redirect validation and restricted egress.
- Other sources, schedules, replacement models and custom rubrics not implemented.

## Judge gate — do not bypass

- Known project:
  `https://danny-ai-svc.services.ai.azure.com/api/projects/danny-ai-svc-project`.
- Unknown: agent name/ID, version/API generation, permitted identity, input and
  output schema, scoring scale/rubric, sanitized response and context limits.
- `JudgeContract` and `UnavailableJudgeAdapter` fail closed. Real adapter still
  needs implementation once verified contract is supplied. Do not enable via
  invented identifiers, replacement model or fabricated scores.
- Policy writes blocked until confirmed scale/rubric; no organizational defaults.
- Acceptance is server-owned and snapshots are immutable. Valid partial decisions
  remain visible; missing/invalid/technical outcomes never fabricate Pass.
- Future adapter must persist invocation IDs before polling, retain private
  diagnostics under approved retention and implement real-protocol reconciliation.

## Validation observed

- `npm run build`: PASS (existing large MapLibre chunk warning).
- `npm run lint`: PASS.
- `npm test`: PASS, 24 files / 216 tests.
- `dotnet test services/evaluation/Evaluation.sln`: PASS, 91 tests.
- `npm run test:evaluation:smoke`: PASS, empty durable DB migrations applied twice,
  real local API liveness, judge blocked, unauthorized routes return 401; cleaned up.
- Isolated PDF CLI smoke: PASS, synthetic valid text extracted; invalid bytes rejected.
- NuGet transitive advisory check: no reported vulnerable packages in four projects.
- `npm audit`: 17 existing-tree findings (4 moderate, 11 high, 2 critical);
  added MSAL packages are not listed. Unrelated dependency upgrades deferred.
- No authenticated live Entra/Graph/Blob/Foundry scan or SQL Server test claimed.
