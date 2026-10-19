# ADR 0112: Hold staged update files across the activation transaction

- Status: Accepted for the activation-transaction foundation; production activation remains blocked
- Date: 2026-10-19

## Context

ADR 0111 added a fresh read-only final-integrity review, but its digest was point-in-time evidence. A same-user process could otherwise replace the staged package or signed manifest after review and before a future operating-system package operation opened the path.

## Decision

The core controlled-update stager exposes a product-owned activation-executor contract and a transaction wrapper. The wrapper requires the exact prior review, the exact readiness receipt, unchanged current version, a separate explicit activation approval and non-regressed UTC time. It opens both final files with read-only sharing, so an existing writer prevents entry and overwrite, replacement, deletion and parent-directory rename remain denied while the executor runs.

While holding both leases, DCx revalidates the exact two-file directory, canonical signed envelope, origin/version/expiry policy, package length/digest and injected package signature/identity/publisher/version immediately before the executor. It repeats the checks after success, failure or cancellation; arbitrary executor exceptions become one stable code. The executor contract requires an operating-system adapter not to return until its package operation is terminal.

Normal desktop composition has no activation executor, UI route or production feed and never calls this transaction. The retained validation executor is inert and only probes Windows sharing behavior. The readiness receipt continues to state `ExecutionPermitted=false` and does not become activation authority.

## Consequences

This closes the staged-file replacement window in the activation-transaction foundation and provides the seam used by ADR 0113's internal uncomposed Windows package adapter. It does not establish production signing-root custody, one-use user activation authority, an invoked `PackageManager` operation, rollback/uninstall, clean-VM lifecycle acceptance, independent security assessment or human JAWS/NVDA/Narrator acceptance. Those remain P13 blockers.

## Validation

Implementation `9f28ae0`, revalidated with ADR 0113 at `fc23b48`, passes 24/24 focused controlled-update/adapter tests, 42/42 P13 reliability tests and the full 682/682 Windows suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-e0abd97bf39849e792dec2f561fa1b37/`. The exact gate recorded zero controlled-update network requests, installer processes, elevation requests, activation attempts, restart attempts and production actions; the real package API was not invoked.
