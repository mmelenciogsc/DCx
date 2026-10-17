# Contribution API local simulation

This configuration is for controlled local/process testing only. It is not a production deployment procedure and does not authorize connecting to the owner's Ubuntu server, exposing a public port or publishing user content.

## Default state

The global service requires its existing `DCX_GLOBAL_SIMULATION=1` gate. Contribution routes are additionally absent unless `DCX_GLOBAL_CONTRIBUTION_API=1`. With the contribution gate off, `/health` reports `contributionApi: false`, `contributionRateLimit: false` and `contributionWorker: false`, while `/api/contributions/*` returns `404`.

## Required simulation settings

Supply secrets through an ephemeral process environment or an approved secret manager; never add tokens, private signing keys or their unredacted values to source, Compose files, shell history or logs.

| Setting | Requirement |
|---|---|
| `DCX_GLOBAL_CONTRIBUTION_API` | Exact value `1` to opt in to simulation routes |
| `DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256` | 64-hex SHA-256 digest of a high-entropy bearer token |
| `DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM` | 1–128 character server-bound identifier using letters, digits, `:`, `.`, `_` or `-` |
| `DCX_GLOBAL_REVIEWER_TOKEN_SHA256` | 64-hex SHA-256 digest of a different high-entropy bearer token |
| `DCX_GLOBAL_REVIEWER_ID` | 1–128 character server-bound reviewer identifier using the same alphabet |
| `DCX_GLOBAL_CONTRIBUTION_REQUESTS_PER_MINUTE` | Optional integer from 10 through 10,000; default 120 |
| `DCX_GLOBAL_CONTRIBUTION_MAX_CONCURRENT` | Optional integer from 1 through 32; default 4 |

The raw bearer tokens are sent only in the HTTP `Authorization: Bearer` header. Contributor and reviewer digest values must differ. Requests use media type `application/vnd.dcx.contribution.v1+binary`; the repository codec is the canonical encoder. Do not manually serialize a production client from this provisional contract.

With the API enabled, `/health` reports `contributionRateLimit: true`. Every contribution route shares a fixed one-minute window and concurrency bound per canonical direct-peer address. The process stores only a SHA-256 partition key, never the raw address or bearer token. It caps memory at 1,024 partitions and returns `429` with one stable code (`rate_limited`, `contribution_busy`, `rate_limit_capacity` or `rate_limit_time_invalid`) without reflecting credentials, addresses or request bodies.

This is process-local simulation protection. State resets on restart, is not synchronized across instances, and deliberately ignores forwarded-address headers. Do not treat it as production reverse-proxy trust, distributed rate limiting or abuse prevention.

## Windows simulation client

The native **Contributions** window can connect only to a root `http` origin on a literal loopback IP, such as `http://127.0.0.1:5087/`. Host names such as `localhost`, paths, HTTPS, remote addresses, user information, query strings and fragments are rejected. The client uses only intake, exact-item status and owner-withdrawal routes; redirects, cookies, proxies and decompression are disabled.

1. Keep **Prepare contributions for review** unchecked unless preparing a deliberate fixture.
2. Enter and review one CKB candidate, complete all three attestations, approve the exact item and prepare it locally.
3. Confirm **Approve sending this exact prepared contribution to the loopback simulation now** is still unchecked.
4. Enter the simulation's matching contributor pseudonym, literal-loopback origin and one-attempt contributor token.
5. Check the separate send approval and invoke **Send to local simulation**.
6. Treat `Quarantined` only as intake confirmation. It is not reviewer approval or publication. The password control and send approval clear after the attempt.
7. For an uncertain result or later state, re-enter the token, check the separate status approval and invoke **Query simulation status**. Status contains bounded lifecycle metadata only, never the contribution body.
8. Do not retry intake unless the original bound origin reports `submission_missing`. Changing the endpoint cannot bypass this origin binding.
9. Reviewer approval remains a separate server role with no contributor-UI control. After a status query reports `Approved`, withdrawal becomes available but remains unchecked.
10. To test deprecation, re-enter the token, check the separate withdrawal approval and invoke **Withdraw from simulation**. Confirm the resulting `Withdrawn` state and release sequence.

The UI has no artifact picker, reviewer-approval action or remote/global endpoint mode. Use only generated fixture credentials. Cancellation may occur after the server accepted a state-changing request; reconcile status before another action. Every password value and action approval clears after the attempt.

## Optional publication worker

The worker is independently default-off. It cannot start unless the contribution API is enabled.

| Setting | Requirement |
|---|---|
| `DCX_GLOBAL_CONTRIBUTION_WORKER` | Exact value `1` to enable the simulation polling loop |
| `DCX_GLOBAL_CONTRIBUTION_WORKER_INTERVAL_MS` | Optional integer from 100 through 60,000; default 1,000 |

With the worker enabled, `/health` reports `contributionWorker: true`. Each cycle processes at most ten CCB review jobs and 100 ready immutable publication intents. Transient failures back off exponentially up to five minutes and dead-letter after five; permanent policy failures dead-letter immediately. The reviewer queues contain identifiers, digests where required for binding, stable codes, counts and timestamps only. They are not production queues or monitoring systems.

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

When both sandbox and worker are enabled, API CCB approval returns `202 Accepted` with a durable job before execution. The worker obtains a 30-minute ownership-fenced claim before extraction or sandbox tests. CCB approval fails with `ccb_review_worker_required` when this queue worker is off. The fixed lease is valid only for the current closed profile of at most 32 tests and 30 seconds per test.

## Supported simulation routes

- Contributor: `POST /api/contributions/intake`, `GET /api/contributions/{submissionId}`, `POST /api/contributions/{submissionId}/withdraw`.
- Reviewer: `GET /api/contributions/pending?limit=`, `GET /api/contributions/{submissionId}`, `POST /api/contributions/{submissionId}/approve`.
- Reviewer worker operations: `GET /api/contributions/publication-queue?limit=`, `POST /api/contributions/publication-queue/{operationId}/retry`.
- Reviewer CCB-review operations: `GET /api/contributions/ccb-review-queue?limit=`, `POST /api/contributions/ccb-review-queue/{jobId}/retry`.

Contributor `GET` returns only submission ID, candidate SHA-256, state, created/reviewed UTC and release sequence. Contributor withdrawal returns that exact submission/candidate binding, `Withdrawn` state and the signed-release descriptor. Reviewer status remains the full review object required for authorized review. These role-dependent response shapes are provisional simulation contracts.

Use the automated child-process test rather than handling credentials manually:

```powershell
Set-Location D:\DCx
dotnet test tests\DCx.Core.Tests\DCx.Core.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~ContributionApiE2eTests
```

The test creates ephemeral credentials, keys, ports and data roots, asserts that sensitive values are absent from captured diagnostics, then deletes the fixture root.
