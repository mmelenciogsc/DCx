# ADR 0035: fail-closed PayPal sandbox boundary

- Status: Accepted for uncredentialed sandbox-contract validation; credentialed acceptance blocked
- Date: 2026-10-17

## Context

CARE needs official webhook verification and PayPal Payouts semantics without allowing fixture success, asynchronous batch status or a provider batch response to become accounting authority. Credentials, merchant eligibility and authorization to transact are not available.

## Decision

Implement one sandbox-only adapter pinned to PayPal's sandbox API origin. Disable redirects, cookies, proxies and decompression; bound time, bodies and JSON; emit only stable body-free error codes; zero owned credential/request/response buffers; and retain live mode as structurally unavailable.

Bind each payout to a stable local batch ID, deterministic request ID and durable unique provider batch reference. Keep `PENDING`/`PROCESSING` reserved. A batch-level `SUCCESS` is insufficient: reconciliation must match the expected provider/local IDs, complete item count, sender item IDs, USD amounts and successful item statuses before posting the cash movement. Any mixed/partial outcome stays reserved and requires a future item-level workflow.

## Consequences

- Handler-backed tests can validate the official contract shape and failure policy without credentials or financial side effects.
- A crash after provider acceptance can reconcile by provider reference instead of duplicating a payout.
- Provider response bodies and credentials are excluded from durable errors and logs; transient managed header strings cannot be securely erased.
- Credentialed PayPal acceptance, subscription mapping, merchant/recipient eligibility, immutable owner-approved recipient snapshots and partial-payout accounting remain explicit P11 blockers.
