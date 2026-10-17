# ADR 0043: Immediate-transaction CARE replay

- Status: Accepted for shared-SQLite local validation; authenticated distributed operations blocked
- Date: 2026-10-18

## Context

ADR 0042 bound exact reviewed health to replay under a repository-instance semaphore. Two independently opened repository objects pointing at the same SQLite ledger did not share that semaphore, and the reprocessor committed each record separately. A competing process could therefore race between review and replay, while a corrupt later record could leave earlier records committed without a complete result.

## Decision

Begin a non-deferred SQLite transaction before the reviewed-health reread. The transaction owns the aggregate reread, exact reviewed-value comparison, due-record selection, all normalization/accounting/audit/quarantine transitions, resulting-health reread and result-contract validation. Commit only after the complete result is valid. Retry, blocked and completed updates now receive that transaction instead of opening per-record transactions. The direct local reprocessor uses the same whole-batch transaction.

SQLite's immediate writer reservation serializes independently opened repository connections to the same database. After the first operation commits, a waiting competitor rereads current health and rejects its stale reviewed value. Any exception, cancellation, digest failure or invalid result before commit rolls back every transition in the batch. The 100-record maximum remains unchanged, and replay still uses only previously verified durable data with no provider call.

## Consequences

- Tests race two independent repository instances against one reviewed value; exactly one completes and accounting posts once.
- A tampered later record is evaluated after an otherwise applicable earlier record; the exception rolls back the earlier audit, ledger and quarantine transition.
- The transaction can hold the SQLite writer reservation for a bounded batch and therefore needs production latency/contention monitoring.
- This coordinates connections to one SQLite database only. It adds no authenticated endpoint, operator identity, scheduler, alert, cross-database/distributed-service fencing or provider acceptance.
