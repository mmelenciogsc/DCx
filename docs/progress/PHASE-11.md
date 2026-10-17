# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (verified-event mapping and durable local accounting pass; credentialed PayPal sandbox acceptance, eligibility, product/plan provisioning and production policy remain unavailable)
- Implementation/validation commits: `2f97540`, `3984c6b`, `4b05cc0`, `dc25a87`, `c021520`, `df1c0b8`, `9877a60`, `faf6b71`, `1631ebb`, `599cee9`
- Documentation commit: recorded by the commit containing this file

No PayPal credential was accessed, no PayPal API request was made, and no subscription, payment or payout was attempted. The only PayPal network activity was six bounded read-only GET requests to official developer documentation/schema URLs.

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
- The same immutable parsed event object is both sent for official verification and mapped after verification, closing caller-field substitution and post-verification mutation gaps. A verification `FAILURE` cannot produce a ledger event.
- Current documented subscription lifecycle events require exact resource type, status, plan ID, subscription ID and RFC 3339 occurrence time. Activation binds the provider subscription to the one configured plan; payment cannot create that binding and fails closed until activation has established it.
- A completed US$3 sale binds its provider sale ID to that subscription. Refunds/reversals must resolve through the durable sale binding, cannot substitute a subscription, and cannot cumulatively exceed recorded sale or subscription receipts. Rejected corrections roll back their identifiers and mappings atomically.
- Each owner-approved payout item now binds a unique immutable recipient-snapshot ID and SHA-256 of the exact destination. At send time, the verified/eligible sandbox directory record must match snapshot ID, contributor and destination digest before credentials are acquired. The durable preview stores no plaintext email address.
- Payout submission requires unique syntactically valid email addresses, exact USD amounts and bounded batch totals. Stable `sender_batch_id`, sender item IDs and a hashed `PayPal-Request-Id` make retries deterministic. Lookup also verifies each returned receiver against the approved destination digest.
- A schema-v5 repository stores subscription-to-plan, sale-to-subscription, correction-object and cumulative financial bindings in addition to the schema-v4 unique local-to-provider payout reference, result digest, paid total and exact per-item outcomes. A crash after PayPal acceptance but before local return reconciles through the provider reference instead of resending.
- `PENDING`, `PROCESSING` and pending/unclaimed/on-hold item states remain reserved and never post cash. A completed mixed result posts only successful item amounts, retains failed amounts in contributor payable, persists item status, and becomes `partially_succeeded`; no automatic retry occurs.
- Schema-v3 reserved batches lack recipient bindings and migrate to `review_required` rather than being resent. Schema-v5 tables are additive; unknown future CARE schema versions fail closed. Version 1→2→3→4→5 migration and reopen behavior are tested.

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
- P11 exact-namespace tests: passed 42, failed 0, skipped 0; retained TRX interval about 0.98 seconds.
- P11 evidence: `artifacts/test-results/p11-ebfd4387848a448c8825b2313f4b9ea0/p11-finance.trx` (generated from the exact source committed as `1631ebb` and intentionally not committed).
- Full native suite: passed 313, failed 0, skipped 0; about 7 seconds in the recorded post-commit run.
- Official-contract evidence: `artifacts/test-results/paypal-contract-c2619695def04e9c9302b09f203d5538/paypal-contract-audit.json`; official sources 6, credentials used 0, PayPal API requests 0, subscription/payment/payout attempts 0.
- Adapter tests cover exact request shapes, sandbox-origin pinning, redirect/origin denial, bounded JSON, duplicate properties, credential/provider-text non-disclosure, recipient-snapshot substitution before credential use, idempotent retry, durable reference recovery, pending state, complete success, receiver/amount mismatch and mixed item outcomes.
- Repository tests prove a 500-cent success plus 700-cent failure posts only 500 cents to cash, leaves 700 cents payable, survives reopen with exact item states, accepts exact replay and denies a conflicting terminal result.
- Verified-event tests prove activation→US$3 sale→partial refund across repository reopen, deny wrong-plan activation, malformed/unverified events and excessive corrections, then accept a corrected reuse of an identifier whose rejected transaction was rolled back.
- Payment, refund, entitlement, balanced-entry and payout state survive repository reopen. Conflicting replay IDs fail closed, and a rejected invalid event does not consume identifiers.

## Changed files in the latest milestone

- Official sandbox boundary and verified mapping: `src/DCx.Core/Finance/PayPalSandboxHttpAdapter.cs`; fixture policy and correction cap: `src/DCx.Core/Finance/PayPalSandboxWebhook.cs`.
- Pending-aware payout contract and coordinator: `src/DCx.Core/Finance/PayoutSafety.cs`.
- Durable schema-v5 plan/sale/correction and recipient/result binding, partial posting and migration quarantine: `server/DCx.GlobalService/DurableCareRepository.cs`.
- Adapter, causality, replay, migration and denial coverage: `tests/DCx.Core.Tests/Finance/PayPalSandboxHttpAdapterTests.cs`, `tests/DCx.Core.Tests/Finance/DurableCareRepositoryTests.cs`.
- Exact-namespace and official-reference gates: `scripts/validate-p11.ps1`, `scripts/audit-paypal-contract.ps1`.
- Contract record: `docs/finance/PAYPAL_SANDBOX_CONTRACT.md`, `docs/architecture/adr/0035-fail-closed-paypal-sandbox-boundary.md`, `docs/architecture/adr/0036-verified-paypal-subscription-bindings.md`.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility evidence or verified recipient directory is available. The adapter is intentionally unconfigured and unwired; official sandbox acceptance has not occurred.
- Verified provider-event-to-local-subscription mapping now exists, but no official product/plan creation or retrieval adapter, approved exact sandbox product/plan ID, credentialed delivery, out-of-order-event quarantine/retry queue or provider acceptance evidence exists. Payment before the bound activation intentionally fails closed.
- The immutable snapshot ID and destination digest are now durable, but no production snapshot directory, protected destination store or keyboard/screen-reader-accessible preparation/owner-approval UI is wired. Those controls must exist before credentials are provisioned.
- Initial mixed outcomes are accounted item by item without automatic retry. A separately approved retry/hold/remediation and contributor-notification workflow is absent. Terminal batches are not polled for later `RETURNED`, `REFUNDED` or `REVERSED` transitions, so post-terminal correction entries remain blocked.
- Profit definition, reserves, cost attribution, thresholds, held balances, post-close adjustments, tax reporting, contributor classification, privacy retention and regional payout rules require accounting/legal approval.
- Unblock only after explicit approval to provision sandbox credentials, configure and validate the exact US$3 sandbox product/plan, evidence merchant/Payouts eligibility, implement the accessible recipient-snapshot workflow, add separately approved failed-item/event retry and post-terminal payout-reversal reconciliation, and run authorized sandbox happy/failure/refund/chargeback/duplicate/crash-window acceptance. Live mode remains disabled pending separate production approval.
