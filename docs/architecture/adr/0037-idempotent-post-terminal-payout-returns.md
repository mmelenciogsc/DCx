# ADR 0037: Idempotent post-terminal payout returns

- Status: Accepted for uncredentialed sandbox reconciliation; provider acceptance blocked
- Date: 2026-10-17

## Context

A PayPal payout item reported as successful can later be returned, refunded or reversed. DCx previously finalized the first terminal result and polled only reserved batches. A later correction could therefore leave cash understated and contributor payable cleared even though the recipient did not retain the funds. Blindly reopening arbitrary terminal states or resending the batch would create duplicate-payment and ledger-integrity risks.

## Decision

Introduced in schema v6 and retained in schema v8, an immutable adjustment row records each corrected item. A digest-verified `succeeded` or `partially_succeeded` batch may be looked up again through its existing provider reference, but this path never submits or resubmits a payout. Provider `NotFound` fails closed. Provider-pending results make no mutation.

A previously successful item may transition exactly once to the exact provider status `RETURNED`, `REFUNDED` or `REVERSED`. Unsuccessful items cannot become successful or change status, and still-successful items cannot be rewritten. The approved preview, stored terminal-result digest, recipient snapshot, destination digest, amounts, item ordering, provider batch status and paid total must all reconcile before mutation.

Each admitted transition atomically updates the item and batch result, records the old/new provider statuses and amount, and posts one balanced compensating journal for the newly returned total: debit cash and credit contributor payable. The journal event ID is derived from the canonical new result digest. Exact replay is a no-op; conflicting replay fails closed. A fully returned batch becomes failed with zero paid cents, while its adjustment history preserves why.

## Consequences

- Cash and contributor payable remain balanced when an initially successful payout is later returned.
- Reconciliation cannot promote failed items, invent amounts, rewrite history or trigger another send.
- The local coordinator is callable and fixture-validated, but no production scheduler, credentialed PayPal acceptance, contributor notification or failed-item retry policy is claimed.
- Provider and adjustment identifiers/statuses remain sensitive accounting metadata subject to retention and access policy.
