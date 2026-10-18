# ADR 0076: Bind payouts to registered CARE allocations

- Status: Accepted for the local CARE sandbox ledger; authoritative close approval and production PayPal acceptance remain blocked
- Date: 2026-10-18

## Context

Schema v12 prevented aggregate contributor-payable over-reservation, but aggregate coverage could not prove that an item belonged to the named contributor or earning period. A preview could substitute a contributor, month, or amount while still fitting the total liability.

## Decision

Schema v13 registers each `care:YYYY-MM` period once from its exact economics and canonical qualified weights. Registration, deterministic contributor allocations, threshold holds, and the balanced contributor-payable accrual commit in one immediate SQLite transaction. Exact replay is inert; conflicting economics, weights, allocations, or ledger content fail closed.

Every new payout preview carries the CARE period identifier. Reservation recomputes the registered allocation set, verifies its digest and Net 60 date, checks each contributor's unreserved payable allocation, and stores immutable batch/ordinal/period/contributor/amount bindings in the same immediate transaction. Completion, pending/terminal recovery, later-return reconciliation, and failed-item remediation revalidate those bindings. Remediation must retain the source period and date. Successful unreturned items and pending reservations consume allocation; failed or returned items release it for a separately approved retry.

Legacy schema-v12 terminal payouts remain readable and reconcilable without invented allocation lineage. They cannot be used as authority for a new reservation.

## Consequences

- Aggregate liability remains a second independent ceiling; contributor allocation and ledger liability must both cover the payout.
- Concurrent repository instances cannot reserve the same contributor-period amount twice even when other contributor liability remains available.
- Below-threshold allocations cannot be paid. Contributor, period, amount, eligibility, stored allocation, and binding substitution fail before provider use.
- The local registration API is not authoritative production close approval. Adjustments, carry-forward holds, disputes, tax/reporting, production identity, distributed fencing, provider credentials, PayPal eligibility, and accounting/legal acceptance remain blocked.
- No provider request, payment, payout, deployment, signing, installation, or production activation is authorized by this decision.

## Alternatives rejected

- Rely on aggregate liability only: it permits contributor and period substitution.
- Recompute allocations from payout previews: payout requests are not authoritative earnings inputs.
- Backfill legacy terminal payouts with guessed periods: invented financial lineage would be misleading.
- Reserve allocation after provider send: external value could move before the local constraint is enforced.
