# ADR 0018: durable pre-sandbox CCB review jobs

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

CCB approval previously executed package tests inline before the durable signed-publication intent existed. Process-local locking could not prevent two server processes from duplicating expensive hostile execution. The authenticated API must never route around a durable claim, and its queue must not duplicate candidate or artifact bodies.

## Decision

Require the simulation worker for API CCB approval. Persist a deterministic job binding submission, candidate digest, server-derived reviewer and request time, then return `202 Accepted` before any sandbox execution. Store no candidate or artifact body in the job.

Acquire a same-SQLite ownership-fenced lease before extraction and execution. The 30-minute lease exceeds the closed package profile's maximum 32 tests at 30 seconds each. Execute at most ten ready jobs per cycle, then enter the existing immutable publication-intent path only after every sandbox test and output digest passes.

Use bounded retry/dead-letter metadata and reviewer-only status/remediation. A permanent policy/test failure dead-letters immediately; twenty expired crash claims also dead-letter. Remediation resets the claim budget but cannot change job identity, candidate digest, reviewer or request time. Malformed persisted job and lease state fails closed.

## Consequences

- Concurrent local service instances sharing the governance SQLite database execute one claimed deterministic fixture rather than duplicate sandbox work.
- A crash before publication can cause the claimed job to run again only after its lease expires; content-addressed artifacts and later publication intents remain independently idempotent.
- The legacy direct governance approval method remains for in-process tests and must not become an external endpoint.
- Fixed leases, same-SQLite coordination and static simulation identity are not a distributed broker, production heartbeat, alerting, retention or high-availability design.
