# Contribution API local simulation

This configuration is for controlled local/process testing only. It is not a production deployment procedure and does not authorize connecting to the owner's Ubuntu server, exposing a public port or publishing user content.

## Default state

The global service requires its existing `DCX_GLOBAL_SIMULATION=1` gate. Contribution routes are additionally absent unless `DCX_GLOBAL_CONTRIBUTION_API=1`. With the contribution gate off, `/health` reports `contributionApi: false` and `contributionWorker: false`, while `/api/contributions/*` returns `404`.

## Required simulation settings

Supply secrets through an ephemeral process environment or an approved secret manager; never add tokens, private signing keys or their unredacted values to source, Compose files, shell history or logs.

| Setting | Requirement |
|---|---|
| `DCX_GLOBAL_CONTRIBUTION_API` | Exact value `1` to opt in to simulation routes |
| `DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256` | 64-hex SHA-256 digest of a high-entropy bearer token |
| `DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM` | 1–128 character server-bound identifier using letters, digits, `:`, `.`, `_` or `-` |
| `DCX_GLOBAL_REVIEWER_TOKEN_SHA256` | 64-hex SHA-256 digest of a different high-entropy bearer token |
| `DCX_GLOBAL_REVIEWER_ID` | 1–128 character server-bound reviewer identifier using the same alphabet |

The raw bearer tokens are sent only in the HTTP `Authorization: Bearer` header. Contributor and reviewer digest values must differ. Requests use media type `application/vnd.dcx.contribution.v1+binary`; the repository codec is the canonical encoder. Do not manually serialize a production client from this provisional contract.

## Optional publication worker

The worker is independently default-off. It cannot start unless the contribution API is enabled.

| Setting | Requirement |
|---|---|
| `DCX_GLOBAL_CONTRIBUTION_WORKER` | Exact value `1` to enable the simulation polling loop |
| `DCX_GLOBAL_CONTRIBUTION_WORKER_INTERVAL_MS` | Optional integer from 100 through 60,000; default 1,000 |

With the worker enabled, `/health` reports `contributionWorker: true`. Each cycle processes at most 100 ready immutable intents. Transient failures back off exponentially up to five minutes and dead-letter after five; permanent policy failures dead-letter immediately. The reviewer queue contains identifiers, stable codes, counts and timestamps only. It is not a production queue or monitoring system.

## Optional Linux CCB sandbox

CKB review works without an execution backend. A CCB can enter quarantine, but approval returns `ccb_sandbox_review_required` unless all of the following are configured on Linux:

| Setting | Requirement |
|---|---|
| `DCX_GLOBAL_CCB_SANDBOX` | Exact value `1` |
| `DCX_GLOBAL_CCB_BASH_PATH` / `DCX_GLOBAL_CCB_BASH_SHA256` | Admitted Bash path and expected SHA-256 |
| `DCX_GLOBAL_CCB_LAUNCHER_PATH` / `DCX_GLOBAL_CCB_LAUNCHER_SHA256` | Policy launcher path and expected SHA-256 |
| `DCX_GLOBAL_CCB_BUBBLEWRAP_PATH` / `DCX_GLOBAL_CCB_BUBBLEWRAP_SHA256` | Bubblewrap path and expected SHA-256 |
| `DCX_GLOBAL_CCB_RUNTIME_PATH` / `DCX_GLOBAL_CCB_RUNTIME_SHA256` | Static runtime path and expected SHA-256 |
| `DCX_GLOBAL_CCB_TIMEOUT_SECONDS` | Integer from 1 through 30 |

The executor rehashes every executable before every run. A mismatch fails closed. The hostile local gate is reproducible with `scripts/validate-p10.ps1`; enabling the optional service path does not waive the production controls listed in `docs/progress/PHASE-10.md`.

## Supported simulation routes

- Contributor: `POST /api/contributions/intake`, `GET /api/contributions/{submissionId}`, `POST /api/contributions/{submissionId}/withdraw`.
- Reviewer: `GET /api/contributions/pending?limit=`, `GET /api/contributions/{submissionId}`, `POST /api/contributions/{submissionId}/approve`.
- Reviewer worker operations: `GET /api/contributions/publication-queue?limit=`, `POST /api/contributions/publication-queue/{operationId}/retry`.

Use the automated child-process test rather than handling credentials manually:

```powershell
Set-Location D:\DCx
dotnet test tests\DCx.Core.Tests\DCx.Core.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~ContributionApiE2eTests
```

The test creates ephemeral credentials, keys, ports and data roots, asserts that sensitive values are absent from captured diagnostics, then deletes the fixture root.
