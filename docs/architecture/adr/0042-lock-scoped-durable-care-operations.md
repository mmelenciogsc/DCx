# ADR 0042: Lock-scoped durable CARE operations bridge

- Status: Accepted for single-repository local validation; authenticated distributed operations blocked
- Date: 2026-10-18

## Context

ADR 0041 defined an aggregate-only accessible operations contract and deliberately kept the normal Windows source unavailable. Schema v8 separately exposed durable aggregate quarantine health and local verified-event replay. Calling refresh and replay independently would allow the reviewed state to change between approval and mutation. Connecting the desktop directly to SQLite or exposing an unauthenticated administrative endpoint would violate the product trust boundaries.

## Decision

Add a server-side `DurableCareOperationsSource` that implements the provider-neutral Core contract over one `DurableCareRepository`. Its processing capability defaults disabled. Refresh maps only the identifier-free aggregate health snapshot; it does not return event, account, provider or contributor identity.

Processing requires the complete reviewed health value, a separate explicit approval and an explicitly enabled server capability. Under the repository instance's mutation semaphore, the repository rereads health, requires exact value equality, requires due work, performs a bounded local replay and rereads resulting health. The result must reconcile all scanned outcomes, remain at or below 100 records and report provider calls as zero. A stale review, disabled capability, missing approval or replay after completion fails before another ledger mutation. Simultaneous submissions against the same repository instance serialize so only one can apply.

The replay continues to use only already verified, digest-bound durable data. It does not acquire PayPal credentials or call PayPal. The Windows application remains wired to `UnavailableCareOperationsSource` in normal mode; this decision adds no desktop database access, network endpoint, scheduler or production authority.

## Consequences

- Handler-backed tests now exercise the real durable aggregate/replay boundary while proving approval denial, default-disabled capability, stale-review denial, concurrent double-submit denial, one-time accounting and zero additional provider calls.
- The mutation semaphore is process- and repository-instance-local. It is not a cross-process or distributed lease, and the bounded batch is not one database-wide transaction.
- Production still requires authenticated and authorized administrative transport, cross-instance serialization/fencing, operator audit/retention policy, scheduling and alerts, accessible blocked-event/remediation disposition, contributor notification, credentialed PayPal sandbox acceptance and human assistive-technology acceptance.
- ADR 0043 supersedes the same-database instance-local limitation with one immediate SQLite transaction; distributed-service fencing and all production gates remain unresolved.
