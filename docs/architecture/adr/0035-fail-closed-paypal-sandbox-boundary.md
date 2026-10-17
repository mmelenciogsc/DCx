# ADR 0035: fail-closed PayPal sandbox boundary

- Status: Accepted for uncredentialed sandbox-contract validation; credentialed acceptance blocked
- Date: 2026-10-17

## Context

CARE needs official webhook verification and PayPal Payouts semantics without allowing fixture success, asynchronous batch status or a provider batch response to become accounting authority. Credentials, merchant eligibility and authorization to transact are not available.

## Decision

Implement one sandbox-only adapter pinned to PayPal's sandbox API origin. Disable redirects, cookies, proxies and decompression; bound time, bodies and JSON; emit only stable body-free error codes; zero owned credential/request/response buffers; and retain live mode as structurally unavailable.

Bind each owner-approved item to an immutable recipient-snapshot ID and exact destination digest, and require the eligible directory record plus provider receiver to match. Bind each payout to a stable local batch ID, deterministic request ID and durable unique provider batch reference. Keep `PENDING`/`PROCESSING` and pending item states reserved. A batch-level `SUCCESS` is insufficient: reconciliation must match provider/local IDs, complete item count, sender item IDs, receivers and USD amounts. A terminal mixed result posts only successful items, leaves failed amounts payable and records exact item states without automatic retry.

## Consequences

- Handler-backed tests can validate the official contract shape and failure policy without credentials or financial side effects.
- A crash after provider acceptance can reconcile by provider reference instead of duplicating a payout.
- Provider response bodies and credentials are excluded from durable errors and logs; transient managed header strings cannot be securely erased.
- Credentialed PayPal acceptance, subscription mapping, merchant/recipient eligibility, a production protected snapshot directory plus accessible approval UI, separately approved failed-item retry/remediation and post-terminal reversal reconciliation remain explicit P11 blockers.
