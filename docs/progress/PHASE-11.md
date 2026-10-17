# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (fail-closed official-sandbox HTTP boundary and durable local accounting pass; credentialed PayPal sandbox acceptance, eligibility, subscription integration and production policy remain unavailable)
- Implementation/validation commits: `2f97540`, `3984c6b`, `4b05cc0`, `dc25a87`, `c021520`, `df1c0b8`, `9877a60`
- Documentation commit: recorded by the commit containing this file

No PayPal credential was accessed, no PayPal API request was made, and no payment or payout was attempted. The only PayPal network activity was three bounded read-only GET requests to official developer documentation/schema URLs.

## Implemented

- US$3 monthly subscription event fixture with signed deterministic webhook mock, strict transmission/event replay semantics, bounded identifiers and exact event/amount/currency/time validation.
- Refund/reversal journal and entitlement suspension; checked integer-cent double-entry ledger with balanced-entry validation and reconciliation.
- SQLite authoritative mock ledger using WAL and `synchronous=FULL`, immutable canonical event hashes, durable postings, minimized decisions, entitlements and payout state.
- Shared causal entitlement reducer: newer signed events apply, stale events cannot overwrite current state, and same-time deactivation dominates activation.
- Positive distributable-net-profit formula and deterministic allocation capped at 4,000 basis points (40% aggregate), with exact cent conservation, threshold holds and Net 60 eligibility.
- Sandbox-only payout coordinator requiring account eligibility, explicit owner approval and Net 60; `Live` is structurally disabled.
- Restart-safe payout reservation/reconciliation with digest-bound previews, stable local batch IDs and durable terminal state.
- Official-contract-shaped PayPal sandbox adapter for OAuth client credentials, webhook-signature verification, payout submission and payout lookup. Its origin is fixed to `https://api-m.sandbox.paypal.com`; redirects, cookies, proxies and decompression are disabled; request/response sizes, time, JSON depth and schemas are bounded.
- Credential leases own byte arrays and zero them on disposal. Request/response byte buffers are zeroed; provider bodies, credential values and transport exception text never enter stable errors. Transient .NET `string` values used for Basic/Bearer headers cannot be securely erased and are documented as a residual limitation.
- Webhook verification requires the exact official evidence fields, an allowlisted PayPal certificate URL and a duplicate-property-free event object. Only official `SUCCESS`/`FAILURE` verification states are admitted.
- Payout submission requires verified/eligible sandbox recipients, unique syntactically valid email addresses, exact USD amounts and bounded batch totals. Stable `sender_batch_id`, sender item IDs and a hashed `PayPal-Request-Id` make retries deterministic.
- A schema-v3 repository migration stores a unique local-to-provider batch reference. A crash after PayPal acceptance but before local return reconciles through that reference instead of resending.
- `PENDING` and `PROCESSING` remain reserved and never post cash. `SUCCESS` is accepted only after lookup binds provider/local batch IDs, item count, sender item IDs, USD amounts and a successful status for every transaction. Mixed or partial results fail closed with `paypal_sandbox_partial_payout_requires_item_reconciliation`.
- Unknown future CARE schema versions fail closed. Version 1→2→3 migration and reopen behavior are tested.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p11.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

The separate official-contract audit is read-only and must not be confused with a PayPal API test:

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-paypal-contract.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P11 exact-namespace tests: passed 34, failed 0, skipped 0; retained TRX interval about 1.62 seconds.
- P11 evidence: `artifacts/test-results/p11-3ff6707526814f0480cef08d69764b50/p11-finance.trx` (generated and intentionally not committed).
- Full native suite: passed 305, failed 0, skipped 0; about 9 seconds in the recorded test run.
- Official-contract evidence: `artifacts/test-results/paypal-contract-0a775d744a4d41369e3cd010675cb36c/paypal-contract-audit.json`; credentials used 0, PayPal API requests 0, payment/payout attempts 0.
- Adapter tests cover exact request shapes, sandbox-origin pinning, redirect/origin denial, bounded JSON, duplicate properties, credential/provider-text non-disclosure, idempotent retry, durable reference recovery, pending state, complete success, mismatched accounting data and partial payout denial.
- Payment, refund, entitlement, balanced-entry and payout state survive repository reopen. Conflicting replay IDs fail closed, and a rejected invalid event does not consume identifiers.

## Changed files in the latest milestone

- Official sandbox boundary and validation: `src/DCx.Core/Finance/PayPalSandboxHttpAdapter.cs`.
- Pending-aware payout contract and coordinator: `src/DCx.Core/Finance/PayoutSafety.cs`.
- Durable schema-v3 provider reference and reconciliation: `server/DCx.GlobalService/DurableCareRepository.cs`.
- Adapter, causality, replay, migration and denial coverage: `tests/DCx.Core.Tests/Finance/PayPalSandboxHttpAdapterTests.cs`, `tests/DCx.Core.Tests/Finance/DurableCareRepositoryTests.cs`.
- Exact-namespace and official-reference gates: `scripts/validate-p11.ps1`, `scripts/audit-paypal-contract.ps1`.
- Contract record: `docs/finance/PAYPAL_SANDBOX_CONTRACT.md`, `docs/architecture/adr/0035-fail-closed-paypal-sandbox-boundary.md`.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility evidence or verified recipient directory is available. The adapter is intentionally unconfigured and unwired; official sandbox acceptance has not occurred.
- No official subscription product/plan creation adapter or provider-event-to-local-subscription mapping exists. The existing US$3 event flow remains deterministic fixture evidence.
- The exact recipient address is resolved at send time but is not frozen durably in the owner-approved preview. Before credentialed acceptance, owner approval must bind an immutable recipient snapshot and revalidation policy.
- Mixed/partial provider outcomes deliberately remain reserved. Item-level accounting, retry/hold/remediation, contributor notification and reconciliation must be designed and tested before any payout attempt.
- Profit definition, reserves, cost attribution, thresholds, held balances, post-close adjustments, tax reporting, contributor classification, privacy retention and regional payout rules require accounting/legal approval.
- Unblock only after explicit approval to provision sandbox credentials, evidence merchant/Payouts eligibility, finish the frozen-recipient and item-level reconciliation design, implement subscription mapping, and run authorized sandbox happy/failure/refund/chargeback/duplicate/crash-window acceptance. Live mode remains disabled pending separate production approval.
