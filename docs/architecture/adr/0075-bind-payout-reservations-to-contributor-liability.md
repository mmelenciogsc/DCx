# ADR 0075: Bind payout reservations to available contributor liability

- Status: Accepted for the local CARE sandbox ledger; contributor-specific accrual authority and production PayPal acceptance remain blocked
- Date: 2026-10-18

## Context

A payout preview was bounded, owner-approved, recipient-bound and Net-60-gated, but durable reservation did not prove that the CARE ledger contained enough outstanding contributor-payable liability. Two service instances could therefore reserve different batches against the same aggregate liability, and a provider send could begin before the accounting constraint was checked.

The preview digest covered each item and amount, but the database had no directly queryable total with which to reserve liability transactionally.

## Decision

CARE schema v12 adds `preview_total_cents` to every current payout batch. New reservations bind it to the validated preview total. Migration re-parses bounded stored previews, revalidates the preview digest and batch identity, and backfills the exact total inside an immediate transaction. Legacy schema-v3 reservations already quarantined as `review_required` may retain zero because they cannot be sent or counted as active reservations.

Initial and failed-item-remediation reservation now use an immediate SQLite transaction spanning batch replay/conflict review, outstanding contributor-payable balance, all active reserved totals and insertion. A batch is admitted only when its complete total fits the remaining unreserved liability. Separate repository instances sharing one database therefore cannot over-reserve the same cents.

Any ordinary ledger posting that changes contributor payable must preserve coverage for every active reservation. Terminal completion rechecks both the stored preview total and the then-outstanding liability before item-state or payout-ledger mutation. Pending and terminal reconciliation also revalidate the stored total.

## Consequences

- A payout cannot reach the sandbox provider through the durable coordinator without an existing aggregate contributor-payable liability covering its complete preview.
- Concurrent batches competing for one liability are serialized at the database write boundary; only the fitting reservation succeeds.
- Pending provider work continues to reserve its full approved amount. A failed terminal result releases the reservation; a partial result posts only successful items and leaves failed liability available for separately approved remediation.
- Schema migration and every payout read fail closed on a conflicting positive preview total. Legacy `review_required` rows remain inert and are excluded from active reserved totals.
- This is an aggregate accounting invariant, not proof that each contributor's preview amount came from that contributor's qualified usage allocation. Production still requires a durable contributor/period allocation subledger, approved adjustment policy, authoritative operator time, distributed fencing, credentialed sandbox acceptance and accounting/legal approval.
- No provider request, credential use, payment, payout, deployment or production activation is authorized by this decision.

## Alternatives rejected

- Check liability only after the provider responds: provider-side value could move before local accounting rejects it.
- Rely on the in-process semaphore: separate repository instances do not share it.
- Sum only completed payouts: pending provider work would not reserve funds and could be overcommitted.
- Infer totals from JSON during each reservation: a schema-bound integer is simpler to query atomically and makes migration integrity explicit.
