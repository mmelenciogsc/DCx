# ADR 0105: Review failed-payout holds without creating payout authority

- Status: Accepted for generated local validation; authenticated remediation, provider execution and external notification remain blocked
- Date: 2026-10-19

## Context

P11 already had durable, exact-item failed-payout remediation lineage and a read-only contributor dashboard. The native operator fixture did not expose a failed-item hold for keyboard and screen-reader review, and the contributor history did not explain each closed status in plain language. Adding those surfaces must not accidentally create a second payout executor, disclose a payout destination, imply that a notice was delivered, or turn generated validation data into production finance state.

## Decision

- Define a provider-neutral failed-payout review contract containing one opaque lowercase review reference, CARE month, integer-cent amount, closed failure/hold/notice/disposition codes, UTC failure time and exact review digest. It has no contributor pseudonym, recipient destination, snapshot, provider account, transaction, free-form note or credential field.
- Keep the normal desktop source unavailable. The explicit P11 fixture may expose one generated record only. It persists nothing beyond process memory and has no credential, provider, payout, finance-ledger or notification transport dependency.
- Permit only `retain_hold`, `request_recipient_update` and `cancel_retry_intent`. Each records operator intent after exact digest review and a separately checked approval. The fixture accepts one decision, disables replay, keeps the payout held and requires provider calls, financial mutations, payout attempts and notification transports all to equal zero.
- Bind every result to the exact request reference and disposition. Default timestamps, malformed identifiers/digests, stale record identity, unapproved decisions, conflicting state and every nonzero authority counter fail closed.
- Mark only a `local_dashboard_notice_ready` state. The read-only contributor history appends fixed plain-language local status notices for withheld, below-threshold, pending, partially paid and paid periods. This is accessible display text, not email, push delivery, provider contact or proof that a person received a notice.
- Expose the review in the existing modeless native CARE operations window with stable UIA names, assertive outcomes, default-unchecked approval, distinct one-handed access keys, denial focus return and post-decision detail focus. Automated UIA must verify content minimization, all zero counters, replay denial and contributor notice text.

## Validation

Implementation commit `a49ff3c` passes five focused policy tests, the native recipient/operations/contributor UIA gate and the complete P11 namespace gate: 132/132 finance tests with zero build warnings/errors. P11 evidence is `artifacts/test-results/p11-56d8ec92d6504778a626d1aeeccf49da/`; schema-v4 recipient UI evidence records 14 distinct operations access keys, approval default off, denied unapproved recording, replay disabled, payout still held and all four authority counters at zero. Full Windows CI passes 657/657.

## Consequences and residual risk

- A keyboard or screen-reader user can now inspect a generated hold, record one bounded non-financial intent and understand local contributor statuses without exposing destinations or granting execution authority.
- `request_recipient_update` is only a closed intent code. No recipient identity, contact channel, durable outbox, delivery receipt, acknowledgment or escalation exists.
- The generated source remains separate from authoritative state. ADR 0106 adds a durable schema-v15 projection and chained decision audit; ADR 0107 subsequently composes that durable source through a default-off authenticated local validation route and isolated native fixture. Neither path is wired into normal desktop startup. Production operator identity/MFA/TLS, authorization, immutable centralized audit, approved accounting policy, recipient notice/subject rights, external delivery and credentialed PayPal sandbox acceptance remain blocked.
- Automated UIA is not human JAWS, NVDA or Narrator acceptance.
