# Evaluation backend

ASP.NET Core API, independently hosted worker, EF Core relational persistence,
private filesystem or Azure Blob object storage, a loopback agent-eval judge
sidecar, and deterministic server-owned acceptance. No data is seeded and no
Azure resources are provisioned by the application.

## Judge integration (agent-eval sidecar)

The judge is the Python evaluator `evals/agent-eval`, exposed by the loopback
HTTP sidecar in `services/judge-sidecar`. In Azure it runs as a second container
in the **worker** Container App at `http://127.0.0.1:8081`; the API app does not
have it and never calls it.

* **Registration heartbeat.** When `Judge__SidecarUrl` is set, the worker fetches
  `GET /contract` at startup and about every 60 seconds, validates it strictly
  (non-empty version, scale exactly 0–100, finite positive weights for exactly
  `structure`/`reference`/`judge`, kinds `dt`/`brd`/`prd`, samples ≥ 1) and
  upserts the single `JudgeRegistrations` row (version, contract JSON, `UpdatedAt`).
  The API is ready, and scans can be created, only while that row is younger
  than 5 minutes. Otherwise readiness reports
  "Judge worker has not registered recently". Without `Judge__SidecarUrl` the
  worker uses a fail-closed adapter and never registers.
* **Snapshots.** Scan creation pins the registered contract (version, layer
  weights, scale, reference, judge deployment/API version/samples), the team
  policy version and the exact input versions. Before invoking, the worker
  re-reads `GET /contract`; a different version fails the scan with
  "Judge contract changed since scan was requested; start a new scan" and no
  judge call.
* **Invocation.** One `POST /evaluate` per artifact (`{kind, files:[{path,text}]}`)
  with extracted text from private storage. DT sends every Markdown file of one
  bundle with its relative path; BRD/PRD send one file. **Source documents are
  never sent** to the judge. `Evaluating` is persisted before the first call; a
  crash or ambiguous interruption becomes `TechnicalFailure` and billable calls
  are never retried automatically. Each call has `Judge__RequestTimeoutMinutes`
  (default 10, 1–60); the scan budget is that × artifacts + 2 minutes. While a
  call is in flight, the 120-second job lease is renewed every 30 seconds; if the
  lease is lost the worker cancels the call and stops without writing. Sidecar
  `invocation_id`s are stored (`RemoteInvocationId`, JSON array) with per-artifact
  token usage. 400/502/500, timeouts, unreachable sidecar and unreadable/oversized
  (16 MB) responses become `TechnicalFailure` / `Inconclusive` with no score; any
  artifacts already validated in that scan stay visible and remaining artifacts are
  marked "Not evaluated". Plain HTTP is accepted only for loopback sidecar URLs.

### Acceptance (server-owned, `Acceptance` in `Evaluation.Core/Domain.cs`)

Per artifact, the response must have `kind` = role, `contract_version` = the
snapshot version, a valid band status, injection/truncation flags, and every
check must have a non-empty id/title, a known layer, a finite score in [0,1], a
finite weight ≥ 0 and a status of `pass`/`warn`/`fail`/`skip`. The server
recomputes agent-eval's scores from the checks with the **snapshot** weights:
layer score = Σ(score·weight)/Σweight over checks with status ≠ `skip` and
weight > 0; artifact score = weighted mean of present layers with weights
renormalized. Reported `score` and `layer_scores` must match within 1e-6 and the
AI judge layer must be scored. Any violation → that artifact is `Inconclusive`
with errors (no score, no checks shown).

`Score100 = recomputed × 100` (full precision stored, displayed to one decimal).
Decision is **Pass** iff `Score100 ≥ threshold` for that role (inclusive, compared
after rounding to 9 decimals to absorb binary floating-point noise), else **Fail**.
Scan outcome: `Inconclusive` if any selected artifact is inconclusive or failed
technically; else `Fail` if any artifact failed; else `Pass`. Unselected roles are
**Not scanned**. Stored per artifact: role, version IDs, score100, threshold,
decision, layer scores/weights, checks (title, layer, status, score, detail,
evidence, guidance), summary, injection flag, truncation flag, usage and errors.
Sidecar strings are untrusted and bounded (titles 300, details 4,000, guidance
2,000, summary 8,000, 20 evidence quotes × 1,000 characters); the SPA renders them
as text only. agent-eval's own pass/warn/fail band is informational only.

## Inputs

* **DT** = one bundle of 1–300 Markdown (`.md`) files uploaded together with a
  relative path per file (folder picker or multi-select). The bundle shares a
  `BundleId`; a DT scan must include every file of exactly one bundle.
* **BRD / PRD** = exactly one file per upload (PDF, DOCX, Markdown or text;
  extracted text is judged). Markdown is recommended because structural checks
  rely on Markdown headings and tables.
* **Source** = optional context-only evidence: uploaded, malware scanned,
  selectable and snapshotted with a scan, but never sent to the judge. One upload
  may be a single file or a folder / multi-select of 1–300 PDF, DOCX, Markdown or
  text files with a relative path per file; the files share a `BundleId`, and each
  file is its own version so a scan can attach a whole upload or any subset (up to
  300 evidence files per scan, across uploads).

PDF, DOCX, UTF-8 Markdown and UTF-8 text extraction runs after the configured
ClamAV INSTREAM service reports a clean verdict. The worker only claims
quarantined uploads while ClamAV answers `PING` (signature loading takes
minutes after container start); if the scanner is unreachable, uploads stay
`Quarantined` rather than being rejected. PDF text uses PdfPig in a disposable
worker child process: 500 pages, one million text characters, 30 seconds and a
sampled 256 MB working-set ceiling. Encrypted documents, macro-enabled packages,
OCR and oversized archive expansion are unsupported. Scanning/extraction failures
reject the version; upload a new version. Original bytes/hash/role/path/bundle
are immutable.

Imports are deliberately blocked: no user-supplied URL is fetched or persisted.

## Configuration

| Key | Used by | Purpose |
|---|---|---|
| `ConnectionStrings__Evaluation` | API, worker | SQLite (default `Data Source=evaluation.db`) or SQL Server connection |
| `Database__Provider` | API, worker | `SqlServer` for SQL Server; anything else uses SQLite |
| `Storage__Provider` | API, worker | `Local` (default) or `AzureBlob` |
| `Storage__Root` | API, worker | Local private directory (Local provider) |
| `Storage__ContainerUri` | API, worker | Existing private HTTPS container URI, no SAS/query (AzureBlob) |
| `AZURE_CLIENT_ID` | API, worker | User-assigned managed identity client ID for `DefaultAzureCredential` (Blob) |
| `Entra__TenantId`, `Entra__Audience`, `Entra__RequiredScope` | API | JWT validation; scope defaults to `Evaluation.Access` |
| `Malware__ClamAvHost`, `Malware__ClamAvPort` | worker | ClamAV INSTREAM endpoint (port default 3310; Azure: `localhost`) |
| `Judge__SidecarUrl` | worker | Sidecar base URL, e.g. `http://127.0.0.1:8081`; unset = fail closed |
| `Judge__RequestTimeoutMinutes` | worker | Per-call timeout, default 10 (1–60) |
| `Spa__Root` | API | Optional built SPA folder; default `<content root>/wwwroot` |
| `Spa__BasePath` | API | SPA asset prefix also served (default `/Apex-Dynamics-Response-Systems`) |
| `Invitations__RedirectUrl` | API | Absolute HTTPS web origin with trailing slash (e.g. `https://host/`; `http://localhost…` allowed for development). Invitees return to `{RedirectUrl}?invitation=<id>#evaluation`. Unset/invalid = every invitation endpoint returns 503 (fail closed) |
| `Invitations__DelegatedClientId` | API | Optional API app registration client ID. When set, invitations are sent on behalf of the signed-in administrator through a secret-free on-behalf-of exchange (see *Sending as the administrator*); unset = the app identity sends them. Also required for directory people search, which only ever uses the caller's delegated token (unset = platform people only) |

`AZURE_CLIENT_ID` is also the identity the API uses for Microsoft Graph B2B invitations (see
[Platform administration and invitations](#platform-administration-and-invitations)).

Both API and worker support `--initialize-database`, which applies the checked-in
EF migrations and exits. Startup never creates schemas, containers or memberships.

### Azure SQL with managed identity

`Microsoft.Data.SqlClient` 5.1 (via EF Core 8 SQL Server) supports
`Authentication=Active Directory Managed Identity`; `User Id` is the client ID of
a user-assigned identity:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<db>;Encrypt=True;Authentication=Active Directory Managed Identity;User Id=<identity client id>
```

Bootstrap SQL (run once by a Microsoft Entra administrator of the database; the
user name is the managed identity's display name):

```sql
CREATE USER [<identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<identity-name>];
ALTER ROLE db_datawriter ADD MEMBER [<identity-name>];
-- Only the identity that runs --initialize-database needs DDL rights:
ALTER ROLE db_ddladmin ADD MEMBER [<identity-name>];
```

The migrations target SQLite and SQL Server (`InitialSchema`, `LiveJudge`, `PlatformTeams`).
SQL Server scripts are generated in tests without a connection; live Azure SQL,
Blob and managed identity access have **not** been verified by this repository.

### Azure Blob

`Storage__Provider=AzureBlob` with `Storage__ContainerUri` uses
`DefaultAzureCredential` (managed identity in Azure, honoring `AZURE_CLIENT_ID`;
approved developer identity locally). Grant **Storage Blob Data Contributor** on
the existing private container only. The app never creates containers or changes
access. Cloud upload timeouts can leave unreferenced blobs; production needs an
approved orphan-reconciliation process.

## Local setup (PowerShell, repository root)

```powershell
$env:ConnectionStrings__Evaluation = "Data Source=$((Get-Location).Path)\services\evaluation\local\evaluation.db"
$env:Storage__Root = "$((Get-Location).Path)\services\evaluation\local\private"
New-Item -ItemType Directory -Force services\evaluation\local | Out-Null
$env:Entra__TenantId = "<tenant GUID>"
$env:Entra__Audience = "<registered API audience>"
$env:Entra__RequiredScope = "Evaluation.Access"
$env:Malware__ClamAvHost = "<operator-controlled scanner host>"
$env:Malware__ClamAvPort = "3310"
$env:Judge__SidecarUrl = "http://127.0.0.1:8081"   # judge-sidecar running locally
dotnet run --project services\evaluation\Evaluation.Api -- --initialize-database
dotnet run --project services\evaluation\Evaluation.Api -- --urls http://localhost:5080
# In another shell with the SAME environment settings:
dotnet run --project services\evaluation\Evaluation.Worker
```

The API serves the built SPA when `wwwroot/index.html` exists (or `Spa__Root`):
static files at `/` and at `Spa__BasePath`, an `index.html` fallback for other
page GETs (never for `/api/*` or `/health`), `X-Content-Type-Options: nosniff`,
`Referrer-Policy: no-referrer`, `frame-ancestors 'none'` on pages and
`frame-ancestors 'self'` on the MSAL `redirect.html` bridge (no COOP). Forwarded
`X-Forwarded-For`/`X-Forwarded-Proto` headers are honored for Azure Container
Apps ingress. `GET /health` is unauthenticated liveness only (`{status:"running"}`).

Entra JWT validation requires HTTPS metadata, issuer, audience, signing key and
lifetime checks. There is **no development authentication bypass**. Every API
route requires the delegated `scp` claim `Entra:RequiredScope`.

First team/administrator bootstrap is an explicit operator database operation:
flag the platform administration team and add its first member (see
[Admin-team bootstrap](#admin-team-bootstrap)). Platform administrators then create
teams, manage memberships and invite people in the workspace's **Administration**
tab. Every signed-in user of the tenant can also join or create teams themselves
(see [Self-service teams and the people picker](#self-service-teams-and-the-people-picker)).
No bootstrap identities or sample teams ship.

## HTTP contract

All `/api` operations require a bearer token. Unauthorized team, resource and
role lookups return 404. Reviewers read; members mutate submissions/documents/
scans; administrators also manage memberships/policies. Platform administrators
(members, in any role, of the platform administration team) can additionally
**read** every team of their tenant; mutations still need a real membership role.
Any signed-in tenant user (valid `tid`/`oid`) may join any non-administration team
and create teams.

| Method | Path (prefix `/api`) | Request / response |
|---|---|---|
| GET | `/me` | `{subject,tenantId,name,isPlatformAdmin,platformAdminTeamId,memberships:[{teamId,teamName,role,isPlatformAdmin}],pendingInvitationCount}`; refreshes stored member `email`/`name` claims |
| GET | `/teams` | `[{id,name,role,isPlatformAdmin,canManage}]`; platform administrators see every tenant team, `role:"PlatformAdmin"` (read-only) where not a member; `canManage` = may edit thresholds and memberships |
| POST | `/teams` | any user; `{name}` (same validation and case-insensitive per-tenant uniqueness as `/admin/teams`; 409 on duplicate). 201 `{id,name,role:"Administrator"}`: the creator becomes the team's **Administrator** only, never a platform administrator. Limited to 10 per user per hour (policy `teams-create`) |
| GET | `/teams/joinable` | any user; `[{id,name,members}]` (member count), sorted by name: every tenant team except the platform administration team and teams the caller already belongs to. Names and counts only |
| POST | `/teams/{team}/join` | any user; adds the caller as **Member** (projects, submissions, uploads, scans) immediately. The caller's pending, unexpired invitations are marked `Accepted` with `acceptedTeamId` = this team in the same transaction. 409 for the platform administration team or an existing membership; 404 for unknown/other-tenant teams. Platform administrators also manage thresholds and memberships in every non-administration team they belong to, whatever their stored role (`/teams` reports `canManage:true`) |
| POST | `/teams/{team}/leave` | any member; removes the caller's own membership (204). 404 when not a member; 409 for the platform administration team or when the caller is the team's last Administrator |
| GET | `/teams/{team}/people?q=` | team managers (see below); `q` trimmed, 2–100 characters (else 400). `{results:[{subject,displayName,email,source:"platform"\|"directory",guest,member}],directoryAvailable,directoryNote}`, at most 20 results de-duplicated by subject |
| POST | `/teams/{team}/invitations` | team managers; `{people:[{subject}\|{email}]}`, 1–20 entries, each exactly one of `subject` (object ID GUID) or `email` (else 400). 200 `{results:[{subject,email,displayName,status:"added"\|"already-member"\|"invited"\|"not-found"\|"error",sentAs,note,redeemUrl,error}]}`; 503 only when an email invitation is needed and `Invitations:RedirectUrl` is unset. Shares the `invitations` limit (30 per user per hour) |
| GET | `/teams/{team}/invitations` | team managers; pending and expired invitations suggesting this team `[{id,email,status,expiresAt,redeemUrl}]` |
| DELETE | `/teams/{team}/invitations/{id}` | team managers; revoke an invitation suggesting this team (404 for others); 204 (idempotent); 409 if already accepted |
| PATCH | `/teams/{team}/invitations/{id}` | team managers; `{suggestedTeamId}` moves a pending invitation suggesting this team to another team the caller manages (400 otherwise, and for the platform administration team) or clears it (`null`). 404 for invitations suggesting other teams; 409 unless stored status is Pending. Sends no email. Returns the admin invitation shape |
| GET/POST | `/teams/{team}/projects` | POST `{name}` |
| GET | `/teams/{team}/projects/{project}` | project |
| GET/POST | `/teams/{team}/projects/{project}/submissions` | POST `{name,description}` |
| GET | `/teams/{team}/submissions/{submission}` | `{submission,documents,scans}` |
| POST | `/teams/{team}/submissions/{submission}/documents` | multipart `role`, one or more `file`; DT and multi-file Source also one `paths` per file (optional for a single Source file). 202 `{bundleId,role,versions}` |
| GET | `/teams/{team}/documents/{version}/download` | attachment only if Ready |
| POST | `/teams/{team}/submissions/{submission}/imports` | 409; never fetches |
| GET | `/teams/{team}/readiness` | `{ready,blockers,judge,lastHeartbeat,scale:{minimum:0,maximum:100},scanner,imports,pdf}` |
| GET | `/teams/{team}/policy` | `{version,thresholds:null\|{version,dt,brd,prd},actor,createdAt}` |
| PUT | `/teams/{team}/policy` | admin; `{expectedVersion,dt,brd,prd}`, each finite 0–100; 409 on version conflict |
| GET/PUT | `/teams/{team}/members` | admin; PUT `{subject:"object-ID",role}`; members include nullable `email`/`displayName` |
| DELETE | `/teams/{team}/members/{subject}` | admin; 204; 409 when removing the last Administrator |
| GET/POST | `/teams/{team}/submissions/{submission}/scans` | POST `{targetVersionIds,evidenceVersionIds}` + `Idempotency-Key` |
| GET | `/teams/{team}/scans/{scan}` | pinned `inputs`, `policy`, `judge`; `result:{outcome,artifacts,errors}`; `invocationIds` |

Document versions expose `id,submissionId,role,fileName,relativePath,bundleId,
hash,size,status,error,textLength,createdAt`. DT and Source paths must be relative, without
`.`/`..`/drive/empty segments, ≤ 260 characters and unique case-insensitively; DT
paths must end in `.md`, Source paths in `.pdf`, `.docx`, `.md` or `.txt`.
Scan requests group targets server-side: at most one DT bundle (all of its
versions), one BRD and one PRD; all Ready and owned by the submission; evidence
must be Source (up to 300 unique versions). Limits: 20 MB/file, 50 versions or bundles, 1,000 files and
100 MB per submission (upload requests up to 101 MB), 5 M characters per judged
artifact, two active scans per team, 60 requests per identity per minute. These
are implementation safety limits, not approved business policy.

## Platform administration and invitations

The **platform administration team** is the single team per tenant with
`Teams.IsPlatformAdmin = 1` (enforced by the filtered unique index
`IX_Teams_TenantId_PlatformAdmin`). It is flagged only by operator SQL: the API
never creates, flags, deletes or offers it to invitees. Every member of it, in any
role, is a platform administrator for that tenant (`tid`); identity is the token
`oid`. All `/api/admin/*` routes return 404 to everyone else.

| Method | Path (prefix `/api`) | Request / response |
|---|---|---|
| GET | `/admin/teams` | `[{id,name,isPlatformAdmin,createdAt,members,projects,submissions,documents,scans}]` |
| POST | `/admin/teams` | `{name}` (1–200 chars, unique per tenant case-insensitively; unique index `(TenantId, Name)`); 201 with `role:"Administrator"`; the creating platform administrator becomes the team's first Administrator; 409 on duplicate |
| DELETE | `/admin/teams/{team}` | body `{confirmName}` = exact name (else 400). 409 for the admin team or while any scan is Queued/Preparing/Evaluating or any outbox row is Pending/Leased. One serializable transaction deletes Outbox, ScanInputs, Scans, Documents, Submissions, Projects, Policies, Audit, Memberships, Team and clears invitation suggestions; private blobs/text are deleted after commit (best effort, failures counted and logged without keys). 200 `{id,rows,objectsDeleted,objectDeleteFailures}` |
| GET | `/admin/teams/{team}/members` | `[{teamId,subject,role,email,displayName}]` |
| PUT | `/admin/teams/{team}/members/{subject}` | `{role}`; subject must be a GUID object ID; 409 if the last Administrator would be demoted |
| DELETE | `/admin/teams/{team}/members/{subject}` | 409 for the last Administrator, or the last member of the platform team |
| GET | `/admin/invitations` | `[{id,email,status,delivery,suggestedTeamId,suggestedTeamName,invitedSubject,invitedBy,createdAt,expiresAt,acceptedAt,acceptedTeamId,acceptedTeamName,redeemUrl}]`; `status` is `Expired` once past `expiresAt`; `redeemUrl` only for pending email invitations |
| POST | `/admin/invitations` | `{email,suggestedTeamId?}` → `{delivery:"email"\|"existing-member",reused,invitation}` |
| POST | `/admin/invitations/{id}/resend` | re-sends the Microsoft email (email deliveries) and extends expiry 14 days |
| DELETE | `/admin/invitations/{id}` | revoke; 204 (idempotent); 409 if already accepted |
| PATCH | `/admin/invitations/{id}` | `{suggestedTeamId}` changes or clears (`null`) the suggested team of a pending (including expired-but-unrevoked) invitation; 400 for the platform team or an unknown team; 409 unless stored status is Pending. Sends no email: the invitee's Join screen preselects the new team, and the UI offers an Outlook draft or Resend. Audited as `InvitationSuggestionChanged` |
| GET | `/invitations/mine` | caller's own pending, unexpired invitations `[{id,suggestedTeamId,suggestedTeamName,expiresAt}]` and `joinableTeams:[{id,name}]` (all tenant teams except the platform team and teams already joined); no emails or inviter identity |
| POST | `/invitations/{id}/accept` | `{teamId}`; the invitation must belong to the caller's `tid`+`oid` (else 404), be Pending and unexpired (else 409); the team must exist and not be the platform team (else 404); 409 if already a member. Creates one `Member` membership; single use under concurrency |

Invitation create/resend (platform console) and adding people to a team
(`POST /teams/{team}/invitations`) share a limit of 30 requests per user per
hour (policy `invitations`); self-service team creation is limited to 10 per user
per hour (policy `teams-create`); other routes keep the 60/minute limit. Team
create/delete, invitation create/refresh/resend/revoke/accept, people added to
teams and admin membership changes are written to `PlatformAudit` (tenant-scoped,
not related to `Teams`, so it survives team deletion).

**Invitation flow (Microsoft Entra B2B; Microsoft sends the email).** The API
looks the address up in the directory (`GET /v1.0/users` by `mail`,
`userPrincipalName` or `otherMails`; falls back to `mail`/`userPrincipalName` if
the advanced query is refused). An existing **Member** user is bound by object ID
without any email (`delivery:"existing-member"`; the administrator shares
`<origin>/#evaluation`). Otherwise it calls `POST /v1.0/invitations` with
`sendInvitationMessage: true` and a short custom message naming the workspace and
suggested team, and binds the returned invited user ID (`delivery:"email"`). A
pending invitation for the same person is updated (suggestion, expiry) instead of
duplicated. After redeeming, the invitee signs in to the workspace and picks a
team on the **Get started** screen (the suggestion is preselected; any
non-administration team can be chosen). Graph 401/403
returns 503 *"Directory invitations are not permitted for the app identity
(User.Invite.All/User.ReadBasic.All admin consent required)"*; other Graph errors
return 502 with a safe message. Tokens, emails and Graph response bodies are never
logged.

**Required permissions.** The API's user-assigned managed identity
(`AZURE_CLIENT_ID`, via `DefaultAzureCredential`, scope
`https://graph.microsoft.com/.default`) needs the Microsoft Graph **application**
permissions `User.Invite.All` and `User.ReadBasic.All` with admin consent. Member
display names/emails come from the API access token's `email` (optional claim),
`preferred_username` and `name` claims; missing claims are tolerated. Live Graph
calls have **not** been exercised by this repository's tests (a fake directory
and an in-process HTTP handler are used).

**Sending as the administrator.** When the app identity sends an invitation,
Microsoft shows the identity's name as the inviter and omits the custom message,
and recipients' mail filters frequently quarantine it. Set
`Invitations__DelegatedClientId` to the API app registration's client ID to send
on behalf of the signed-in administrator instead: the API exchanges the caller's
token on-behalf-of (OBO) for a Graph token, authenticating as the app registration
with a client assertion from the managed identity (federated identity credential,
issuer `https://login.microsoftonline.com/<tenant>/v2.0`, subject = managed
identity object ID, audience `api://AzureADTokenExchange`; no client secret). The
registration needs admin-consented delegated `User.Invite.All`;
`infra/scripts/Register-EntraApps.ps1 -ManagedIdentityPrincipalId` configures both.
If the administrator may not invite guests (tenant setting
*adminsAndGuestInviters*: only admins and Guest Inviters) or the exchange fails,
the app identity sends it and the response carries `sentAs:"app"` with a `note`;
Graph 5xx on the delegated call is returned, not retried. Responses include
`sentAs` (`admin`/`app`). Microsoft only confirms acceptance for sending, never
delivery, so the console also offers an Outlook draft for the administrator to
send personally. Browsers can only pre-fill plain text into a new message
(`mailto:`), so "Email from Outlook" copies the formatted invitation to the
clipboard (HTML with an **Accept invitation** button linking to the same Graph
`inviteRedeemUrl` as Microsoft's invitation email, or the workspace link when
there is none, plus both links as clickable text; plain-text alternative), then
opens a new message with only the recipient and subject. The empty body keeps
the sender's Outlook signature; they paste (Ctrl+V) above it and send.

## Self-service teams and the people picker

**Decision (recorded): open joining.** Anyone signed in to the tenant can join any
team except the platform administration team, as Member, without approval, and
anyone can create a team (becoming its Administrator, not a platform
administrator). As a consequence **any tenant user can read and work with any
non-administration team's projects, submissions, documents and results** by
joining it; team boundaries organize work but are not confidentiality boundaries
between colleagues of one tenant. Tenant isolation is unchanged: every query is
scoped to the caller's `tid`, and other tenants' teams return 404. Platform
administrator powers are unchanged. Joins, leaves, team creation, people added and
invitations are written to the team `Audit` and to `PlatformAudit`
(`PlatformAdminJoined`/`PlatformAdminLeft` only when the actor is a platform
administrator).

The SPA shows a **Get started** screen to signed-in users without any membership
(no invitation is required any more) and from **Join or create a team** in the
workspace: join a team from `/teams/joinable` (with a pending invitation the
suggested team is preselected and joining uses `/invitations/{id}/accept`), or
create one. Every member sees **Leave team** for non-administration teams.

**Who can invite into a team.** Callers who can manage the team: its
Administrators, and platform administrators who joined it (the platform
administration team itself is managed only from Administration; these endpoints
return 409 for it). Everyone else gets 404.

**People picker** (Team settings → Memberships → *Invite people*): an accessible
combobox (debounced 250 ms, 2+ characters) over

- *On the platform*: distinct people with a membership in any team of the caller's
  tenant whose stored display name or email contains the text (case-insensitive);
- *In the directory*: Microsoft Graph `GET /v1.0/users?$search="displayName:…" OR
  "mail:…" OR "userPrincipalName:…"` (`ConsistencyLevel: eventual`, `$top=15`,
  basic profile fields), retried once as a `startswith(...)` `$filter` when Graph
  rejects the search with 400. This always uses the **caller's own delegated
  token** (on-behalf-of exchange, delegated `User.ReadBasic.All`), so the caller's
  directory permissions and guest restrictions apply. It **never** falls back to
  the app identity: without `Invitations__DelegatedClientId`, without a caller
  token, on token-exchange failure or Graph 401/403 the response has
  `directoryAvailable:false` and a `directoryNote`, and only platform people are
  returned. Search text and results are never logged.

Guests are recognized by `#EXT#` in the user principal name; people already in
the team are shown but can't be chosen. A typed email address that matches no
result is offered as *Invite … by email*.

Adding people (`POST /teams/{team}/invitations`) resolves every entry before
writing anything, then processes entries independently:

- **subject**: a platform person of this tenant, or a directory user confirmed by a
  delegated `GET /v1.0/users/{id}` (404 → `not-found`), is added as **Member**
  (`added`, or `already-member`). No email is sent; the SPA offers an Outlook draft
  with the workspace link. Guests who haven't redeemed get just-in-time redemption
  at sign-in.
- **email**: a platform person or an existing directory user (app-identity lookup
  as for platform invitations) is added the same way. Anyone else receives a
  Microsoft B2B invitation (sent on the caller's behalf when allowed, as above),
  recorded as a pending invitation suggesting this team and refreshed instead of
  duplicated (`invited`, with `sentAs`, `note`, `redeemUrl`).
- A Graph failure on one entry makes that entry `error` with a safe message; the
  others continue.

**Required permissions.** In addition to the application permissions above, the
API app registration needs admin-consented **delegated** `User.Invite.All` and
`User.ReadBasic.All` (on-behalf-of) and `Invitations__DelegatedClientId` set.
Live Graph search has **not** been exercised by this repository's tests.

### Admin-team bootstrap

Run after `--initialize-database` applied `PlatformTeams`, as a database Entra
administrator (parameters: tenant GUID, team name, first administrator object ID):

```sql
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @team UNIQUEIDENTIFIER = (SELECT TOP 1 Id FROM Teams WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @tenant AND IsPlatformAdmin = 1);
IF @team IS NULL
BEGIN
    SET @team = NEWID();
    INSERT INTO Teams (Id, TenantId, Name, PolicyVersion, IsPlatformAdmin, CreatedAt) VALUES (@team, @tenant, @name, 0, 1, SYSDATETIMEOFFSET());
END
ELSE UPDATE Teams SET Name = @name, IsPlatformAdmin = 1 WHERE Id = @team;
IF NOT EXISTS (SELECT 1 FROM Memberships WHERE TeamId = @team AND Subject = @admin)
BEGIN
    INSERT INTO Memberships (TeamId, Subject, Role) VALUES (@team, @admin, 0);
    INSERT INTO Audit (Id, TeamId, Actor, Action, Resource, CreatedAt)
        VALUES (NEWID(), @team, 'operator-bootstrap', 'MembershipBootstrap', @admin, SYSDATETIMEOFFSET());
END
COMMIT;
```

`infra/scripts/Deploy-Evaluation.ps1` runs the equivalent statement (it can also
adopt an existing team by name). `PlatformTeams` adds `Teams.IsPlatformAdmin`
(bit, default 0), `Teams.CreatedAt` (datetimeoffset; existing rows get
2026-10-20), nullable `Memberships.Email`/`DisplayName`, the `Invitations` and
`PlatformAudit` tables and their indexes; it seeds no data.

## Durability and operational safety

Scan creation, input relationships, audit and outbox insertion are atomic.
Idempotency keys are unique per team and bound to submission plus canonical
input IDs. Policy updates use an optimistic concurrency token and append-only
versions. Scans pin hashes, relative paths, bundles, policy and judge contract.
The worker consumes the relational outbox directly (no Service Bus) with
compare-and-update claims, owner fencing, lease renewal during long calls and
bounded pre-invocation retries (for example while the sidecar restarts).
Ingestion and scanning run in separate loops so a multi-minute judge call does
not block malware scanning.

Production release still needs live validation of Azure SQL, Blob, managed
identities/RBAC and the sidecar in the target environment, cost/token limits,
backup/restore, parser isolation hardening and approved retention/deletion
(original objects, extracted text, results, scan inputs/outbox in FK order,
backups and audit holds). Historical rows (scans, scan inputs, documents,
policies, audit, platform audit) cannot be deleted through the EF change tracker;
the platform administrator's whole-team deletion (`TeamDeletion` in
`Evaluation.Core/Platform.cs`) is the only approved deletion path. It does not
purge database backups or blob soft-delete/versioning copies.

## Checks

```powershell
dotnet build services\evaluation\Evaluation.sln
dotnet test services\evaluation\Evaluation.sln
```

Tests use isolated SQLite, temporary storage and synthetic identities, contracts
and judge responses in the test assembly only. They cover acceptance boundaries
per role, recomputation mismatch, missing judge layer, invalid scores/statuses/
weights, wrong kind/version, DT bundle completeness, selection rules, registration
freshness, contract changes, adapter HTTP errors/timeouts (stub handler and a real
loopback fake sidecar), lease renewal and loss, ClamAV PING gating, policy and
upload path validation, migrations (SQLite and SQL Server script) and SPA fallback,
platform administration (admin detection, read-only cross-team access, team
create/delete, memberships), invitations through a fake directory, self-service
joining/leaving/team creation, the team people picker and team invitations, the
Graph adapter (including delegated-only people search) against an in-process
handler, and concurrent single-use acceptance.
They do not substitute for Entra, Microsoft Graph, Azure SQL, Blob, ClamAV or live judge tests.
