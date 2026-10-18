# ADR 0077: Bind CARE period close review and approval

- Status: Accepted for the local CARE sandbox ledger; production close authority and PayPal acceptance remain blocked
- Date: 2026-10-18

## Context

Schema v13 bound payouts to deterministic contributor-period allocations, but its public registration path could create the period and payable accrual without a distinct review and approval step. That was suitable only as a calculation fixture, not as a safe financial close boundary. A schema-v13 period also has no durable proof that its exact economics, weights, allocations, close date and Net 60 date were reviewed before new payout work was admitted.

## Decision

Schema v14 replaces direct period registration with a two-step local contract. `ReviewCarePeriodClose` canonicalizes the bounded contributor set, recalculates the exact result and produces a SHA-256 review binding over the inputs, weights, allocations and ten-minute UTC review window. `RegisterReviewedCarePeriodAsync` requires separate explicit approval within that window, recomputes every value, compares the review digest in fixed time and atomically stores a second digest binding the review and exact approval time before posting the payable accrual.

Exact replay of the same approved review is inert. A different review for an already approved month, an altered input/result/weight, absent approval, early or expired approval, partial marker set, approval-time edit or stored digest edit fails closed. Payout reservation recomputes and validates both bindings before it can reserve contributor allocation or aggregate liability.

Migration adds nullable markers so schema-v13 state is not falsely described as reviewed. Existing terminal payouts remain readable and reconcilable. A legacy registered period cannot authorize a new payout until a fresh exact review is separately approved; promotion is atomic, and competing repository instances can bind only one review.

## Consequences

- Period calculation no longer mutates financial state; only a valid, separately approved review can register and accrue a month.
- Review and approval metadata are integrity-bound and checked again at payout admission.
- Schema-v13 history is preserved without inventing approval evidence.
- The boolean local owner-approval contract and SHA-256 bindings are sandbox controls, not authenticated production accounting authority or tamper resistance against a privileged same-user database attacker.
- Adjustment, carry-forward hold, dispute, tax/reporting, production identity, distributed fencing, credentials, PayPal eligibility and legal/accounting acceptance remain blocked.
- No provider request, payment, payout, deployment, signing, installation or production activation is authorized by this decision.

## Alternatives rejected

- Keep direct registration: it conflates calculation with an accrual-authorizing close.
- Backfill schema-v13 rows as approved: that would fabricate financial authorization history.
- Bind only the review: the approval time could be edited without detection.
- Validate only during registration: later payout admission would trust mutable stored markers.
- Reject every schema-v13 period permanently: exact fresh review can safely promote unchanged local fixture state without duplicating accrual.
