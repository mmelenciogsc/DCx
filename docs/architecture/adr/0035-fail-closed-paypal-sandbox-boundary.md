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
- ADRs 0036 and 0037 add verified subscription mapping and local post-terminal return/reversal reconciliation; ADR 0038 adds a protected local owner-approval workflow that deliberately cannot assert provider eligibility; ADR 0039 adds an expiring, separately approved failed-item lineage boundary. Credentialed PayPal acceptance, merchant/Payouts and recipient eligibility, an approved provider-verification/retention policy, an accessible remediation/notification operator workflow and production reconciliation scheduling remain explicit P11 blockers.
