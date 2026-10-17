# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (durable local sandbox model and crash recovery pass; real PayPal sandbox, eligibility and official adapter validation unavailable)
- Implementation/validation commits: `2f97540`, `3984c6b`, `4b05cc0`, `dc25a87`, `c021520`
- Documentation commit: recorded by the commit containing this file

No PayPal credential was accessed, no network call was made, and no payment or payout was attempted.

## Implemented

- US$3 monthly subscription event fixture with signed deterministic webhook mock, strict transmission/event replay semantics, bounded identifiers and exact event/amount/currency/time validation.
- Refund/reversal journal and entitlement suspension.
- Checked integer-cent double-entry ledger with balanced-entry validation, account balances and reconciliation.
- SQLite authoritative mock ledger using WAL and `synchronous=FULL`, immutable canonical event hashes, durable postings, webhook IDs, transmission IDs, entitlements and payout state.
- Restart-safe replay/idempotency and conflicting-event denial.
- Shared causal entitlement reducer: newer signed events apply, stale events cannot overwrite current state, and same-time deactivation dominates activation. A later activation can reactivate normally.
- Minimized durable webhook decision audit containing stable identifiers/type/time, SHA-256 subscription and payload digests, and a reducer decision code—never the webhook body or signature.
- Additive CARE schema-v2 migration records entitlement source provenance, preserves existing state, and rejects unknown future schemas.
- Defined positive distributable-net-profit formula and configurable contributor pool rejected above 4,000 basis points (40% aggregate).
- Deterministic qualified-weight allocation with exact cent conservation, largest-remainder tie-break and below-threshold holds.
- UTC calendar earning month and Net 60 date exactly 60 calendar days after month-end.
- Sandbox-only payout preview/provider boundary requiring account eligibility, explicit owner approval and Net 60; durable reservation/terminal state; duplicate batch rejection; `Live` always disabled.
- Bounded enumeration of digest-verified reserved payout batches after restart, with an explicit provider lookup contract for not-found, pending, succeeded and failed states.
- Deterministic crash-window recovery: a provider-accepted batch is finalized without resending; a provider-not-found batch is resubmitted with its stable batch ID; a provider-pending batch remains reserved.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p11.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P11 exact-namespace tests: passed 18, failed 0, skipped 0; 1,484 ms in the recorded TRX run.
- P11 evidence: `artifacts/test-results/p11-7c1b71dc47cb433eb90f84daaadb5523/p11-finance.trx` (generated and intentionally not committed).
- Current full suite: passed 215, failed 0, skipped 0; about 7 seconds.
- Payment, refund, entitlement, balanced-entry and payout state survive repository reopen.
- Exact event/transmission/content replay is idempotent after restart; reuse of either identifier with conflicting content fails closed. A rejected invalid event does not consume replay identifiers.
- A stale or equal-time activation cannot undo a newer/equal-time deactivation across reopen. Stale refund accounting still posts and reconciles independently without regressing newer entitlement state.
- A version-1 database migrates to schema version 2 without reactivating a newer inactive entitlement; minimized decision audit and future-schema rejection are covered.
- Failed payout creates durable terminal state without a cash posting; successful payout clears contributor payable and reconciles.
- Restart simulations cover crash-before-send, crash-after-provider-acceptance and provider-still-pending states. The accepted case makes zero sends; the not-found case makes one stable-ID resend; a second reconciliation finds no remaining work.

## Changed files in the latest milestone

- Shared validation, replay and causal reducer: `src/DCx.Core/Finance/PayPalSandboxWebhook.cs`.
- Durable decision audit and schema migration: `server/DCx.GlobalService/DurableCareRepository.cs`.
- Causality, replay, migration and future-schema denial coverage: `tests/DCx.Core.Tests/Finance/CareFinanceTests.cs`, `tests/DCx.Core.Tests/Finance/DurableCareRepositoryTests.cs`.
- Exact-namespace retained-TRX gate: `scripts/validate-p11.ps1`.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility or recipient verification is available. Fixture HMAC is not represented as PayPal webhook verification, and the local occurrence-time/tie policy still requires validation against official provider contracts.
- The local provider is deterministic, not PayPal. The durable recovery state machine is tested locally, but its lookup/status mapping and stable-ID resend semantics have not been implemented or validated against the official PayPal sandbox adapter.
- Profit definition, reserves, cost attribution, thresholds, held balances, refunds after close, tax reporting, contributor classification and regional payout rules require accounting/legal approval; see `docs/finance/CARE_DEFINITION.md`.
- Unblock only with approved PayPal sandbox credentials and merchant feature evidence. Then implement official webhook verification and subscription/payout adapters and run happy/failure/refund/chargeback/duplicate/crash-window acceptance tests. Live mode remains disabled pending separate production approval.
