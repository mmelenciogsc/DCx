# ADR 0048: Opaque, non-financial CARE blocked-case disposition

- Status: Accepted for default-off local simulation; production closure/requeue and notification policy remain blocked
- Date: 2026-10-18

## Context

Schema v10 exposed only aggregate blocked-event health. That protected provider and subject identifiers, but an operator could not inspect or acknowledge one exact blocked record accessibly. Exposing event, transmission, subscription, sale, plan, account, recipient or contributor identifiers to the desktop would unnecessarily widen sensitive accounting-data access. Allowing a generic disposition to requeue, reject or apply an event would also create unapproved financial authority.

## Decision

Schema v11 creates a random 128-bit lowercase hexadecimal case reference in the same transaction that first moves a verified event to `blocked`. Startup migration backfills pre-v11 blocked rows in bounded batches. The immutable binding stores the internal event key, opaque case reference, exact review digest and creation time; list operations are read-only and fail closed if any blocked row lacks its binding.

The operator contract returns at most 100 cases with only the opaque reference, closed event kind (`sale`, `refund` or `reversal`), closed reason (`identity_conflict` or `policy_rejected`), bounded attempt count, UTC first/last-attempt times, exact review digest and current fixed disposition. The digest binds the opaque reference to the internal event identity and verified-event digest without exposing those values.

Only `investigation_requested` and `retained_blocked` are accepted. There is no free-form note. Each action requires authenticated simulation transport, exact review-digest comparison and separately explicit approval. Approved changes append the operator-pseudonym digest, fixed disposition, review digest, UTC time and sequence/chain digests to an immutable audit. Exact latest-action replay is inert. SQLite triggers deny ordinary binding/audit update or deletion.

Both dispositions are non-financial operator intent. They do not alter quarantine state, entitlement, ledger, payout, recipient data or aggregate health; they make zero provider calls and expose no closure, rejection, replay or requeue action. The generated native fixture uses the same contract and provides distinct keyboard access keys, default-unchecked approval, assertive status and deterministic focus recovery.

## Consequences

- An operator can accessibly review and classify one exact privacy-minimized case without receiving provider or subject identifiers.
- A GET/list request performs a deferred read transaction only. Case creation is atomic with the original block transition; legacy backfill occurs during schema migration.
- Random case references and review/audit digests are still linkable sensitive pseudonymous data. They are not anonymous and require approved access, retention, notice and subject-rights policy.
- The digest/trigger chains detect ordinary mutation but are not an external signed anchor. A database administrator can still rewrite the database.
- “Investigation requested” has no external ticket, notification, escalation or human acknowledgment transport. “Retained blocked” does not close or discard an event. Production identity/MFA/TLS, centralized audit, distributed deployment, closure/requeue policy, external notification and credentialed PayPal acceptance remain blocked.

## Alternatives rejected

- Expose raw PayPal/event identifiers to the Windows client: unnecessary sensitive-data expansion.
- Accept free-form operator notes: unbounded personal-data and secret leakage risk.
- Allow requeue, rejection, ledger application or financial correction from this surface: no approved accounting/provider policy or production authority.
- Lazily create case references from the GET endpoint: violates read-only request semantics and obscures durable mutation timing.
