# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (deterministic local mocks pass; real PayPal sandbox and durable ledger unavailable)
- Partial implementation commit: `2f97540`
- Documentation commit: recorded by the commit containing this file

No PayPal credential was accessed, no network call was made, and no payment or payout was attempted.

## Implemented

- US$3 monthly subscription event fixture with signed deterministic webhook mock, transmission/event replay denial, entitlement activation/suspension/cancellation and exact plan/currency validation.
- Refund/reversal journal and entitlement suspension.
- Checked integer-cent double-entry ledger with balanced-entry validation, event-ID conflict denial, account balances and reconciliation.
- Defined positive distributable-net-profit formula and configurable contributor pool rejected above 4,000 basis points (40% aggregate).
- Deterministic qualified-weight allocation with exact cent conservation, largest-remainder tie-break and below-threshold holds.
- UTC calendar earning month and Net 60 date exactly 60 calendar days after month-end.
- Contributor expense/payable accrual and sandbox payout journal factories.
- Sandbox-only payout preview/provider boundary requiring account eligibility, explicit owner approval and Net 60; duplicate batch rejection; `Live` always disabled.
- Sandbox provider success and failure fixtures with no live fallback.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p11.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors; 4.89 seconds in the recorded dedicated run.
- P11 tests: passed 6, failed 0, skipped 0; 33 ms.
- Full suite: passed 100, failed 0, skipped 0; about 1 second.
- US$3 happy path, invalid signature, wrong plan, duplicate event/transmission, refund and entitlement suspension passed.
- Balanced journal, 40%-maximum allocation, 3:1 attribution split, zero/negative-net exclusion and Net 60 passed.
- Missing owner approval, pre-Net60, duplicate batch and live mode failed closed; sandbox success/failure stayed sandbox-only.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility or recipient verification is available. The mock HMAC is not represented as PayPal webhook verification.
- The ledger and executed-batch set are in-memory deterministic models, not the required durable immutable authoritative database/reconciliation service. Restart-safe idempotency, corrections, close/reopen and provider reconciliation remain unimplemented.
- Profit definition, reserves, cost attribution, thresholds, held balances, refunds after close, tax reporting, contributor classification and regional payout rules require accounting/legal approval; see `docs/finance/CARE_DEFINITION.md`.
- Unblock only with approved PayPal sandbox credentials and merchant feature evidence. Then implement the official verification API and sandbox subscription/payout adapters, persist double-entry/outbox/reconciliation state, and rerun happy/failure/refund/chargeback/duplicate tests. Live mode must remain disabled until a separate production approval.
