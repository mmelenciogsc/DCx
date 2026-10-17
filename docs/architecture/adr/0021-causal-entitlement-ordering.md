# ADR 0021: Reduce entitlement events in causal order

- Status: Accepted for the local sandbox boundary
- Date: 2026-10-17

## Context

Subscription webhooks can arrive late, be retried, or share a provider timestamp. Writing every accepted event directly to the current entitlement allowed an older activation to undo a newer cancellation, suspension, refund, or reversal. Replay identifiers alone do not establish causal order.

## Decision

DCx reduces entitlement state by the signed event's UTC occurrence time. A newer event may replace current state. An older event is retained in the minimized audit and any independent financial journal is posted, but it cannot change current entitlement. At the same timestamp, deactivation may replace activation and activation can never replace deactivation. A later activation may reactivate normally.

Exact event/transmission/payload replay is idempotent. Reuse of either identifier with different content fails closed. Identifiers are recorded only after the whole transaction succeeds. The authoritative SQLite fixture records the event type, occurrence time, event and transmission identifiers, SHA-256 payload and subscription digests, and a stable reducer decision code; it stores neither webhook bodies nor signatures in this audit.

Schema version 2 adds decision provenance and entitlement source metadata. Additive migration from version 1 is restart-safe, and a future unknown schema version is rejected.

## Consequences

- Delayed activation cannot reopen an entitlement closed by a later event.
- Equal-time ambiguity resolves conservatively toward deactivation.
- A stale refund/reversal still creates its balanced correcting journal entry even when it cannot overwrite newer entitlement state.
- The local HMAC fixture remains only a deterministic model. Production requires official PayPal verification, event/status mapping, sandbox acceptance, credential custody, merchant eligibility, reconciliation, and approved accounting/legal policy.

## Alternatives rejected

- Arrival-order last-write-wins: network delay can regress entitlement state.
- Rejecting stale events entirely: this would lose financial corrections and audit evidence.
- Event-ID lexical ordering: provider identifiers are not a documented causal clock.
