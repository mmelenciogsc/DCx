# Architecture decision index

| ADR | Decision | Status | Date |
|---|---|---|---|
| [0001](architecture/adr/0001-wpf-webview2.md) | Use WPF on .NET 10 with WebView2 for the native shell | Accepted | 2026-10-17 |
| [0002](architecture/adr/0002-local-first-trust-boundaries.md) | Enforce local-first execution and explicit broker boundaries | Accepted | 2026-10-17 |
| [0003](architecture/adr/0003-separate-private-and-global-knowledge.md) | Keep private and global CKB/CCB stores physically and logically separate | Accepted | 2026-10-17 |
| [0004](architecture/adr/0004-openai-auth-and-codex-boundary.md) | Isolate Codex runtime and keep API-key mode independent from gated ChatGPT-plan sign-in | Accepted | 2026-10-17 |
| [0005](architecture/adr/0005-embedded-sqlite-private-store.md) | Use embedded SQLite FTS5 plus bounded managed vectors for private CKB/CCB | Accepted | 2026-10-17 |
| [0006](architecture/adr/0006-signed-global-delta-chain.md) | Synchronize the global replica as a signed, checkpointed, atomically activated delta chain | Accepted | 2026-10-17 |
| [0007](architecture/adr/0007-care-integer-ledger-and-live-disable.md) | Use integer-cent CARE accounting and structurally disable live payouts | Accepted for sandbox | 2026-10-17 |
| [0008](architecture/adr/0008-msix-target.md) | Target signed per-user MSIX packaging; retain MSI only as an evidence-driven fallback | Accepted provisionally | 2026-10-17 |
| [0009](architecture/adr/0009-offline-root-trust-policy.md) | Rotate and revoke global release keys through an offline-root-signed policy chain | Accepted | 2026-10-17 |
| [0010](architecture/adr/0010-closed-app-recovery-lease.md) | Run accessible recovery separately under an exclusive per-data-root lease | Accepted | 2026-10-17 |
| [0011](architecture/adr/0011-aggregate-only-pilot-metrics.md) | Keep pilot metrics default-off, aggregate-only and closed-dimensioned | Accepted for local controls | 2026-10-17 |
| [0012](architecture/adr/0012-read-only-shared-readiness-policy.md) | Share one immutable readiness policy between reports and a read-only native dashboard | Accepted for local controls | 2026-10-17 |
| [0013](architecture/adr/0013-rootless-server-ccb-sandbox.md) | Isolate contributed code on the authoritative Linux service with a digest-bound rootless sandbox policy | Provisional; local hostile gate only | 2026-10-17 |
| [0014](architecture/adr/0014-private-local-crash-diagnostics.md) | Keep crash diagnostics default-off, content-free, bounded and local with accessible consent/purge | Accepted for local controls | 2026-10-17 |
| [0015](architecture/adr/0015-publication-intent-fencing-leases.md) | Coordinate authorized publication intents with expiring ownership-fenced leases | Accepted for same-SQLite local orchestration | 2026-10-17 |
| [0016](architecture/adr/0016-default-off-contribution-simulation-api.md) | Expose contribution governance only through a default-off, role-separated simulation API | Accepted for local simulation | 2026-10-17 |
| [0017](architecture/adr/0017-minimized-publication-retry-dead-letter.md) | Retry immutable publication intents with bounded, body-free failure state | Accepted for local simulation | 2026-10-17 |
| [0018](architecture/adr/0018-durable-pre-sandbox-ccb-review-jobs.md) | Claim digest-bound CCB review jobs before hostile package execution | Accepted for local simulation | 2026-10-17 |
| [0019](architecture/adr/0019-bounded-simulation-contribution-pressure.md) | Bound simulation contribution requests without credential-derived partitions | Accepted for local simulation | 2026-10-17 |
| [0020](architecture/adr/0020-controlled-update-admission.md) | Require dual manifest/package trust before inert update staging | Accepted for pre-install admission only | 2026-10-17 |
| [0021](architecture/adr/0021-causal-entitlement-ordering.md) | Reduce entitlement events by occurrence time with deactivation-safe ties | Accepted for local sandbox | 2026-10-17 |
| [0022](architecture/adr/0022-digest-bound-local-model-inference.md) | Bind local inference to a unique model name and SHA-256 digest before and after execution | Accepted for installed local models | 2026-10-17 |
| [0023](architecture/adr/0023-explicit-persisted-local-model-approval.md) | Require accessible explicit persisted approval for exact installed-model identity and license | Accepted for already-installed local models | 2026-10-17 |
| [0024](architecture/adr/0024-windows-trust-policy-revision-anchor.md) | Anchor the minimum accepted global trust-policy revision in Windows Credential Manager | Accepted for per-user Windows file-rollback defense | 2026-10-17 |
| [0025](architecture/adr/0025-explicit-loopback-contribution-submission.md) | Require a separate approval and literal-loopback boundary for native contribution submission | Accepted for local simulation | 2026-10-17 |

Decisions are revisited only when measured constraints or authoritative requirements change. Superseded ADRs remain in the repository.
