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
| [0026](architecture/adr/0026-minimized-owner-contribution-lifecycle.md) | Reconcile and withdraw exact owner contributions through minimized, separately approved loopback operations | Accepted for local simulation | 2026-10-17 |
| [0027](architecture/adr/0027-path-private-ccb-artifact-review.md) | Review and submit exact CCB artifacts without retaining their local path | Accepted for local simulation | 2026-10-17 |
| [0028](architecture/adr/0028-local-only-visible-web-video-frame-qa.md) | Pause and question only the visible web-video rectangle through an approved local vision model | Accepted for deterministic fixtures; live provider blocked | 2026-10-17 |
| [0029](architecture/adr/0029-inert-exact-package-install-review.md) | Review an exact digest-bound package request without creating installation authority | Accepted for local validation; real install blocked | 2026-10-17 |
| [0030](architecture/adr/0030-bounded-windows-msix-trust-inspection.md) | Bind bounded MSIX manifest identity to real Windows signature evidence before approval | Accepted for read-only admission; signed positive blocked | 2026-10-17 |
| [0031](architecture/adr/0031-no-remediation-release-scanning.md) | Require trusted no-remediation malware scans and official dependency-advisory auditing for release staging | Accepted for local release staging; independent assessment blocked | 2026-10-17 |
| [0032](architecture/adr/0032-reproducible-unsigned-self-contained-msix.md) | Build a reproducible unsigned self-contained MSIX without creating install authority | Accepted for inert local validation; signing/install blocked | 2026-10-17 |
| [0033](architecture/adr/0033-explicit-whisper-file-registration.md) | Register an already-installed Whisper backend by exact approved runtime, model and license files | Accepted for fixture-backed registration; real backend blocked | 2026-10-17 |
| [0034](architecture/adr/0034-explicit-ffmpeg-file-registration.md) | Require exact executable/license/signature review before bounded FFmpeg media processing | Accepted for bounded local consumers; real approval blocked | 2026-10-17 |
| [0035](architecture/adr/0035-fail-closed-paypal-sandbox-boundary.md) | Bind official PayPal sandbox contracts to durable, item-checked payout reconciliation | Accepted for uncredentialed contract validation; credentialed acceptance blocked | 2026-10-17 |
| [0036](architecture/adr/0036-verified-paypal-subscription-bindings.md) | Map only officially verified PayPal events through durable plan, subscription and sale bindings | Accepted for uncredentialed handler-backed validation; credentialed acceptance blocked | 2026-10-17 |
| [0037](architecture/adr/0037-idempotent-post-terminal-payout-returns.md) | Reconcile later payout returns through one-way item corrections and compensating journals | Accepted for uncredentialed sandbox reconciliation; provider acceptance blocked | 2026-10-17 |
| [0038](architecture/adr/0038-protected-owner-approved-recipient-snapshots.md) | Protect exact payout destinations behind immutable, default-off owner-approved snapshots | Accepted for local owner approval; provider verification and payout use blocked | 2026-10-17 |
| [0039](architecture/adr/0039-explicit-failed-payout-item-remediation.md) | Remediate only exact failed payout items through an expiring separately approved lineage | Accepted for durable uncredentialed sandbox validation; operator/provider acceptance blocked | 2026-10-17 |
| [0040](architecture/adr/0040-durable-verified-webhook-dependency-quarantine.md) | Quarantine only officially verified events whose durable subscription or sale dependency has not arrived | Accepted for uncredentialed handler-backed validation; production operations/provider acceptance blocked | 2026-10-17 |
| [0041](architecture/adr/0041-fixture-only-accessible-care-operations.md) | Validate accessible aggregate CARE operations through a default-unavailable, generated-data-only source | Accepted for native generated-data validation; authenticated production operations blocked | 2026-10-18 |
| [0042](architecture/adr/0042-lock-scoped-durable-care-operations.md) | Bind exact reviewed aggregate state to bounded durable local replay under one repository lock | Accepted for single-repository local validation; authenticated distributed operations blocked | 2026-10-18 |
| [0043](architecture/adr/0043-immediate-transaction-care-replay.md) | Serialize reviewed CARE replay across shared-SQLite connections and commit the bounded batch atomically | Accepted for shared-SQLite local validation; authenticated distributed operations blocked | 2026-10-18 |
| [0044](architecture/adr/0044-authenticated-loopback-care-operations-api.md) | Expose aggregate CARE review/replay only through a separately gated, digest-authenticated loopback simulation API | Accepted for default-off local simulation; production identity and deployment blocked | 2026-10-18 |

Decisions are revisited only when measured constraints or authoritative requirements change. Superseded ADRs remain in the repository.
