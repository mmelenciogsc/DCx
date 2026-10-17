# ADR 0047: Persist aggregate CARE alerts with at-least-once delivery receipts

- Status: Accepted for shared-SQLite local simulation; external notification and distributed scheduling remain blocked
- Date: 2026-10-18

## Context

ADR 0046 introduced a read-only aggregate monitor, but process-memory deduplication could not distinguish a delivered alert from a crash before delivery after restart. Advancing state before sink success risks loss; advancing only in memory causes every restart to repeat nonclear state. An external notification provider is neither authorized nor configured, and individual CARE/provider identifiers are unnecessary for aggregate operational attention.

## Decision

Schema v10 adds three minimized structures in the existing CARE SQLite database:

- a singleton observation state containing only status, aggregate fingerprint, observation UTC and state digest;
- an immutable sequence- and previous-digest-chained aggregate alert outbox; and
- separate immutable, digest-bound delivery receipts keyed by the exact outbox sequence and record digest.

Observation runs in an immediate transaction. It first returns the oldest undelivered record, if any. Otherwise it validates the prior state, suppresses an unchanged fingerprint, atomically updates the observation state and appends an alert only for a new attention state or recovery. Initial clear remains silent. The fingerprint excludes the cumulative completed count; an alert record retains the validated complete aggregate snapshot.

After sink success, a second immediate transaction appends an exact delivery receipt. Receipts must form a gap-free prefix and cannot predate observation. Exact repeated acknowledgment is inert; changed bindings or out-of-order acknowledgment fail closed. A crash after sink success but before receipt may redeliver the same stable sequence and digest, providing at-least-once rather than exactly-once behavior.

SQLite triggers deny ordinary alert/receipt update or deletion. Full history, chain, record/delivery digests, composite sequence+record binding and state digest are validated on use. Separate repository connections to one SQLite database serialize through immediate transactions.

## Consequences

- Pending aggregate attention survives service/repository restart and sink failure without automatic CARE processing or provider calls.
- The outbox contains fixed alert/severity/status codes, aggregate counts/retry depth/times/capability, observation UTC and integrity digests. It contains no token, request/response body, exception, operator, event, provider, plan, subscription, sale, account, recipient or contributor identifier.
- Delivery is intentionally at least once; future external sinks must use sequence+record digest as an idempotency key and tolerate duplicates.
- Triggers and hashes are tamper-evident local controls, not an external signed anchor. Database administrators can rewrite controls/history, and tail truncation remains possible without an external checkpoint.
- There is no outbox retention/pruning policy, external transport, recipient routing, acknowledgment by a human, escalation, accessible disposition UI, SLA, multi-database consensus or production deployment. Those remain blockers.
