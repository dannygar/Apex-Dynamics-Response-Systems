# Apex Dynamics Response Systems

[![Deploy to GitHub Pages](https://github.com/AmieDD/Apex-Dynamics-Response-Systems/actions/workflows/deploy.yml/badge.svg)](https://github.com/AmieDD/Apex-Dynamics-Response-Systems/actions/workflows/deploy.yml)

> Live demo: https://amiedd.github.io/Apex-Dynamics-Response-Systems/

## Project Titan Watch: A mocked emergency-response command-center prototype

<img src="public/Clipzilla.png" alt="Clipzilla, the Apex Dynamics Response Systems mascot, holding a tablet displaying the command-center dashboard" width="320">

## What this repo is
Apex Dynamics Response Systems is a command-center prototype for an emergency disaster response scenario. Apex Dynamics Response Systems — fuses imagery, sensor networks, incident reports, and public social signals into a single situational picture for first responders: damage assessment, response prioritization, evacuation planning, and shared situational awareness.
The prototype is a command-center view — situational dashboard, multi-source feed, severity telemetry. Mocked feeds. No live connections.

This repo is an Vite + React frontend and only that. Everything is mocked end to end: no backend, no live data, nothing production-grade. That's the point. 

Renders ≠ verified. A demo that looks done and code that's production-ready are different artifacts.

## Background research

The [docs/pre-prototype](docs/pre-prototype/) folder holds the discovery calls, email chains, and stakeholder interviews that came before this prototype. They explain why the command-center view looks the way it does and which concerns it left unresolved. Read them in numbered order, starting with the [initial discovery call](docs/pre-prototype/01-call-initial-discovery.md).

## Getting Started

### Prerequisites

* Node.js 22.18 or newer (enforced by the `engines` field in `package.json`; CI runs Node 22).
* npm (bundled with Node; this repo ships a `package-lock.json`).
* Git, to clone the repository.
* Internet access for `npm ci` and, at runtime, for the map basemap tiles.

No `.env` file, API keys, or backend are required for the existing command-center
prototype. The separate **Evaluation** workspace uses an authenticated backend
and has no mock scores or fallback judge.

### Install and run

```bash
# Clone the repository
git clone https://github.com/AmieDD/Apex-Dynamics-Response-Systems.git

# Change into the repository directory
cd Apex-Dynamics-Response-Systems

# Install dependencies exactly as pinned in package-lock.json
npm ci

# Start the dev server with hot reload at http://localhost:5173/
npm run dev

# Expose the dev server on your local network
npm run dev -- --host
```

### Other scripts

```bash
# Type-check (tsc -b) then build to dist/
npm run build

# Serve the production build at http://localhost:4173/ (run build first)
npm run preview

# Lint the project
npm run lint

# Run the Vitest suite once
npm test
```

### Notes

* `npm ci` requires the lockfile and wipes `node_modules` first. Use `npm install` only when you intend to change dependency versions.
* `npm run dev` does not type-check, while `npm run build` runs a strict `tsc -b` first, so a build can fail even when the dev server runs cleanly.
* The map basemap loads tiles over HTTPS from `tiles.openfreemap.org` (OpenFreeMap: MIT style, OpenStreetMap/ODbL data). Offline, the map renders blank but the rest of the app still loads.

## Evaluation workspace

Select **Evaluation** in the workspace navigation (`#evaluation`). The original
command center remains available, with its state retained while switching.
Without identity configuration, Evaluation shows prerequisites rather than sample
teams, results, or invented agent references.

### Implemented

* MSAL Browser organization sign-in and same-origin bearer-authenticated API client.
* Authorized team/project/submission navigation, creation, private version uploads,
  quarantine/extraction status, DT Markdown bundle (folder) uploads, per-artifact selection and downloads.
* Manual review/start flow with retry-stable idempotency keys; polling; historical
  run details and side-by-side comparison with captured policy/judge contract/input hashes.
* Separate DT/BRD/PRD thresholds (0–100) and membership management for administrators.
* ASP.NET Core API, separate worker, EF migrations, local SQLite or configurable SQL
  Server, durable transactional outbox and fenced worker leases. Local storage is
  private filesystem storage; an optional Azure Blob SDK provider uses
  `DefaultAzureCredential` against an **existing** container, never provisions one.
* Malware-gated PDF/DOCX/Markdown/text extraction. PDF runs in a disposable child
  process with bounded pages, text, time and sampled memory usage; no OCR/encrypted PDFs.
* Live judge: the worker calls the agent-eval judge sidecar (`services/judge-sidecar`)
  and registers its contract; the server recomputes layer/artifact scores, applies
  inclusive per-type thresholds (score × 100) and marks invalid or technical
  outcomes Inconclusive. Source documents are context only and never judged.
  No default organizational thresholds or data seeds.

### Local setup

1. Install a .NET 8 SDK alongside Node. This implementation session installed SDKs
   locally under `%TEMP%\apex-dotnet`; it did not modify machine-wide installations.
2. Follow [backend setup](services/evaluation/README.md) to configure an empty
   database, apply migrations and run API (`http://localhost:5080`) and worker.
   No development authentication bypass exists.
3. Register Entra SPA and API applications, configure delegated API scope/consent
   and matching `Entra__TenantId` / `Entra__Audience` on the API. Configure access-token
   v2 issuance for the API. `Entra__RequiredScope` defaults to `Evaluation.Access`
   (the token's delegated scope name, not its full `api://…` URI).
   Bootstrap the first team administrator as an operator.
4. Copy entries from [.env.evaluation.example](.env.evaluation.example) to
   `.env.local`, supplying public tenant ID, SPA client ID and full delegated API
   scope. No client secrets or access keys belong in browser variables.
5. Register the exact SPA redirect URI, including path and port:
   `http://localhost:5173/Apex-Dynamics-Response-Systems/redirect.html`
   (adjust host/port for your environment). MSAL v5 uses this dedicated redirect
   bridge; do **not** send COOP headers on that page. Also register the app base
   URI for post-logout navigation. Vite builds both HTML entry points.
6. Run `npm run dev`. The `/api` development proxy targets localhost:5080.
   Production requires an approved same-origin reverse proxy/API host; static
   GitHub Pages alone cannot supply the API.
7. Configure an operator-controlled ClamAV INSTREAM scanner for the worker.
   Until it is configured, uploaded versions remain quarantined and unusable.

Checks (from repository root):

```text
npm run build
npm run lint
npm test
dotnet test services/evaluation/Evaluation.sln
npm run test:evaluation:smoke
```

`test:evaluation:smoke` builds the API, applies migrations twice to a temporary
empty database, starts a bounded API process, verifies health and unauthenticated
denial, then stops it and deletes temporary data. Set `DOTNET_HOST_PATH` to a local
SDK's `dotnet` executable if it is not on PATH. Tests use synthetic data only in
test assemblies; nothing is seeded into the application.

### Explicit remaining integration/release blockers

* **Judge:** scans need a worker with `Judge__SidecarUrl` and a healthy agent-eval
  sidecar that registered within 5 minutes; otherwise scanning fails closed. The
  sidecar integration is tested only against fakes here, not against a live model.
* **Identity:** real tenant/app registrations, consent, membership bootstrap and
  authenticated end-to-end validation remain prerequisites. Automated API tests
  use test-only signed JWTs, not proof of live Entra access.
* **Connectors:** SharePoint/OneDrive, Blob-link imports and allowlisted public
  HTTPS ingestion are explicitly disabled, not working connectors. Consent,
  scopes and reviewed provider-specific implementations are still required.
  Other providers are not implemented.
* **Cloud/operations:** Azure Blob provider and SQL Server are compiled but not
  cloud-tested. Azure RBAC/private-container/network settings and approved
  resources are required. Service Bus transport is not implemented; the local
  worker consumes the durable relational outbox directly. Production also needs
  hard parser isolation, distributed quotas, approved retention/legal holds and
  verified deletion/orphan reconciliation, backups, diagnostic retention and
  judge invocation reconciliation. Proposed limits are safety ceilings, not
  approved business policies.

No Azure resources were provisioned or deployed. No live scan is claimed.

Validation/dependency note: build, lint, 216 frontend tests and 91 backend tests
passed in the implementation session, along with the local API/migration and
isolated PDF smoke checks. NuGet reported no vulnerable packages with the
configured sources. `npm audit` still reports 17 findings in the existing
dependency tree (4 moderate, 11 high, 2 critical); the added MSAL packages are not
listed. Unrelated dependency upgrades were not bundled into this feature.
These findings and the live-integration gates must be resolved/reviewed before
claiming production readiness.