# ADR 0068: Roll back every admitted dependency-transfer failure

- Status: Accepted for the inert core transport; real acquisition remains blocked
- Date: 2026-10-18

## Context

ADR 0063 required an empty, seekable destination and rollback after transfer failure. The initial implementation handled expected transport, I/O, cryptographic, cancellation and stable transfer exceptions. Two edge cases remained. It attempted rollback even when destination admission had failed, which could truncate pre-existing caller data. Conversely, an unexpected exception type from a progress observer or custom stream could bypass rollback after bytes had been admitted. An unexpected exception during rollback could also expose an unstable exception rather than the explicit cleanup-failure state.

## Decision

Track whether the destination has passed the empty, writable and seekable admission check. A rejected destination is never modified. Once admitted, every ordinary exception path attempts to set its length and position to zero and flush it without honoring a canceled operation token. Expected caller cancellation remains cancellation only when cleanup succeeds; timeouts and transfer failures retain their stable codes.

Do not restrict rollback to an exception allowlist. Unexpected callback, response-stream, destination-stream and framework exceptions receive the same cleanup attempt and stable `dependency_transfer_failed` surface. Rollback itself catches arbitrary ordinary exceptions and returns only `dependency_transfer_cleanup_failed`. That code explicitly means the stream could not be proven empty and the caller must discard or quarantine it. The production set coordinator already removes its entire random incoming operation directory on any such failure, so no incomplete set can publish.

Hostile synthetic contracts require that a nonempty rejected destination retain its original byte and position, an unexpected progress callback failure restore an admitted destination to empty, and an unexpected rollback failure return only the cleanup-failure code without claiming cleanup. They make no network request outside the injected handler.

## Consequences

- DCx no longer destroys caller-owned content merely because a destination was invalid.
- New exception types cannot silently bypass the rollback attempt after destination admission.
- A cleanup failure is distinguishable from a normal transfer failure and never permits publication or reuse of the destination.
- Catastrophic process termination cannot be converted into an in-process guarantee; atomic set publication and startup recovery remain required.
- This decision grants no provider, download, extraction, installation, registration, selection or execution authority.
