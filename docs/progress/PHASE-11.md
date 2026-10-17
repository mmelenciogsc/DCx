# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (fail-closed official-sandbox HTTP boundary and durable local accounting pass; credentialed PayPal sandbox acceptance, eligibility, subscription integration and production policy remain unavailable)
- Implementation/validation commits: `2f97540`, `3984c6b`, `4b05cc0`, `dc25a87`, `c021520`, `df1c0b8`, `9877a60`, `faf6b71`
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
- Each owner-approved payout item now binds a unique immutable recipient-snapshot ID and SHA-256 of the exact destination. At send time, the verified/eligible sandbox directory record must match snapshot ID, contributor and destination digest before credentials are acquired. The durable preview stores no plaintext email address.
- Payout submission requires unique syntactically valid email addresses, exact USD amounts and bounded batch totals. Stable `sender_batch_id`, sender item IDs and a hashed `PayPal-Request-Id` make retries deterministic. Lookup also verifies each returned receiver against the approved destination digest.
- A schema-v4 repository stores a unique local-to-provider batch reference, result digest, paid total and exact per-item outcomes. A crash after PayPal acceptance but before local return reconciles through the provider reference instead of resending.
- `PENDING`, `PROCESSING` and pending/unclaimed/on-hold item states remain reserved and never post cash. A completed mixed result posts only successful item amounts, retains failed amounts in contributor payable, persists item status, and becomes `partially_succeeded`; no automatic retry occurs.
- Schema-v3 reserved batches lack recipient bindings and migrate to `review_required` rather than being resent. Unknown future CARE schema versions fail closed. Version 1→2→3→4 migration and reopen behavior are tested.

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
- P11 exact-namespace tests: passed 39, failed 0, skipped 0; retained TRX interval about 1.73 seconds.
- P11 evidence: `artifacts/test-results/p11-ea8039c246074da2962bafebe9d40f31/p11-finance.trx` (generated from the exact source committed as `faf6b71` and intentionally not committed).
- Full native suite: passed 310, failed 0, skipped 0; about 8 seconds in the recorded test run.
- Official-contract evidence: `artifacts/test-results/paypal-contract-0a775d744a4d41369e3cd010675cb36c/paypal-contract-audit.json`; credentials used 0, PayPal API requests 0, payment/payout attempts 0.
- Adapter tests cover exact request shapes, sandbox-origin pinning, redirect/origin denial, bounded JSON, duplicate properties, credential/provider-text non-disclosure, recipient-snapshot substitution before credential use, idempotent retry, durable reference recovery, pending state, complete success, receiver/amount mismatch and mixed item outcomes.
- Repository tests prove a 500-cent success plus 700-cent failure posts only 500 cents to cash, leaves 700 cents payable, survives reopen with exact item states, accepts exact replay and denies a conflicting terminal result.
- Payment, refund, entitlement, balanced-entry and payout state survive repository reopen. Conflicting replay IDs fail closed, and a rejected invalid event does not consume identifiers.

## Changed files in the latest milestone

- Official sandbox boundary and validation: `src/DCx.Core/Finance/PayPalSandboxHttpAdapter.cs`.
- Pending-aware payout contract and coordinator: `src/DCx.Core/Finance/PayoutSafety.cs`.
- Durable schema-v4 recipient/result binding, partial posting and migration quarantine: `server/DCx.GlobalService/DurableCareRepository.cs`.
- Adapter, causality, replay, migration and denial coverage: `tests/DCx.Core.Tests/Finance/PayPalSandboxHttpAdapterTests.cs`, `tests/DCx.Core.Tests/Finance/DurableCareRepositoryTests.cs`.
- Exact-namespace and official-reference gates: `scripts/validate-p11.ps1`, `scripts/audit-paypal-contract.ps1`.
- Contract record: `docs/finance/PAYPAL_SANDBOX_CONTRACT.md`, `docs/architecture/adr/0035-fail-closed-paypal-sandbox-boundary.md`.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility evidence or verified recipient directory is available. The adapter is intentionally unconfigured and unwired; official sandbox acceptance has not occurred.
- No official subscription product/plan creation adapter or provider-event-to-local-subscription mapping exists. The existing US$3 event flow remains deterministic fixture evidence.
- The immutable snapshot ID and destination digest are now durable, but no production snapshot directory, protected destination store or keyboard/screen-reader-accessible preparation/owner-approval UI is wired. Those controls must exist before credentials are provisioned.
- Initial mixed outcomes are accounted item by item without automatic retry. A separately approved retry/hold/remediation and contributor-notification workflow is absent. Terminal batches are not polled for later `RETURNED`, `REFUNDED` or `REVERSED` transitions, so post-terminal correction entries remain blocked.
- Profit definition, reserves, cost attribution, thresholds, held balances, post-close adjustments, tax reporting, contributor classification, privacy retention and regional payout rules require accounting/legal approval.
- Unblock only after explicit approval to provision sandbox credentials, evidence merchant/Payouts eligibility, implement the subscription mapping and accessible recipient-snapshot workflow, add separately approved retry and post-terminal reversal reconciliation, and run authorized sandbox happy/failure/refund/chargeback/duplicate/crash-window acceptance. Live mode remains disabled pending separate production approval.
