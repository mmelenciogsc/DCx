# ADR 0069: Keep progress observers non-authoritative after transfer commit

- Status: Accepted for the inert transfer-set coordinator; real acquisition remains blocked
- Date: 2026-10-18

## Context

The transfer-set coordinator invokes a caller-supplied progress observer throughout transfer and verification. Before publication, an observer exception correctly aborts the operation and removes the private incoming directory. After `Directory.Move` atomically publishes an anchored complete set, however, the same exception cannot truthfully turn that durable commit into a failed transfer. The previous implementation propagated a `Published` observer exception, leaving a valid set while telling the caller that transfer failed; a retry then encountered the immutable destination.

The equivalent `Completed` notification occurs after every protected receipt, inventory, length and digest verification has succeeded. An observer exception at that point should not replace the completed verification result either.

## Decision

Keep progress observers non-authoritative. Before the commit boundary, observer exceptions continue to abort and use the existing cleanup path. After atomic publication, deliver `Published` on a best-effort basis and return the committed receipt even if the observer throws. After full verification, deliver `Completed` on the same best-effort basis and return the verified receipt.

Do not move either notification before its truthful state transition. Do not delete or roll back an atomically published set because non-authoritative UI code failed after commit. A hostile contract throws private-detail exceptions at both post-commit stages and requires one publication, a successful return, and subsequent protected verification.

## Consequences

- The API result cannot contradict durable set state solely because a progress observer failed after commit.
- Pre-commit observer failures still fail closed and remove incomplete state.
- The native workflow remains responsible for separately reverifying the published set before announcing end-user completion.
- Catastrophic process termination and same-user filesystem attacks remain outside this in-process guarantee.
- This decision grants no provider, download, extraction, installation, registration, selection or execution authority.
