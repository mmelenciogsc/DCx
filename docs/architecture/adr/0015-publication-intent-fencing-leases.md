# ADR 0015: publication-intent fencing leases

- Status: Accepted for same-SQLite local orchestration
- Date: 2026-10-17

## Context

P10 already persists exact reviewer-authorized approval and withdrawal intents before mutating the separate signed registry. Stable registry operation identifiers prevent duplicate releases across a crash, but process-local locks do not stop two governance processes from executing the same pending intent or a stale worker from completing after takeover.

## Decision

Keep publication authority and execution coordination separate. The immutable intent continues to bind submission, action, reviewer where applicable, requested time and canonical change digest. A separate SQLite lease row carries only a random process owner, random fencing token, acquisition/expiry times and bounded attempt count.

Claims are atomic and apply only to incomplete intents. A lease lasts five minutes and is renewed around registry work. Contribution state and intent completion advance in one transaction only when that transaction observes the same unexpired owner/token; successful completion clears active lease fields. A crash leaves the lease to expire, ordinary failures release it, and an expired stale worker cannot complete after another process reclaims the intent. Attempts stop at 20. Malformed lease rows fail before reclaim rather than being overwritten.

The registry's digest-bound idempotent operation ID remains the final duplicate-release defense. A lease never creates, expands or changes reviewer authorization.

## Consequences

- Concurrent processes sharing one local SQLite governance database can recover the same intent without duplicate completion.
- Time jumps can cause early or late takeover, but fencing plus idempotent registry operations preserve correctness.
- There is still no continuous worker, wakeup/backoff scheduler, operator dead-letter UI or supported multi-node/network-filesystem coordination.
- CCB sandbox review occurs before the publication intent exists and remains protected only by a process-local approval lock; a durable pre-sandbox workload queue is still required for production.
