# ADR 0057: Stage approved Whisper runtimes as inert digest-bound trees

- Status: Accepted for non-elevated local staging only
- Date: 2026-10-18

## Context

ADR 0056 rejects unsafe Whisper runtime archives before extraction, but a safe archive review alone does not define where bytes may be written, how a reviewed source remains stable during extraction, what survives failure, or whether staging accidentally grants installation or execution authority. P06 needs a testable boundary before any real dependency transfer is authorized.

## Decision

Require a separate explicit user approval for staging. Before writing, bind the request to a catalog-valid Whisper runtime candidate and an existing local ZIP with the exact pinned length. Copy that source under a no-write/no-delete lease into a randomly named private file within non-reparse per-user application storage. Authenticate and structurally inspect the private copy under an exclusive lease, then extract it into a randomly named sibling directory using the same entry-path policy as the inspector.

Create every output with `CreateNew`, recompute its CRC-32 and SHA-256 while writing, preserve neither archive attributes nor timestamps, and compare the complete file, directory and expanded-byte inventory with the authenticated review. Write a bounded receipt containing candidate, archive, license, inventory and per-file digest facts but no selected source path. Remove the private archive, revalidate the tree, and publish it with a same-volume directory move to an immutable candidate-and-digest-derived destination. Never overwrite an existing destination.

On failure, cleanup may inspect and remove only the generated direct-child temporary tree. It must first reject reparse points and out-of-root entries and must never recursively follow a link merely to clean up. Unsafe leftovers remain inert for later user review. The service does not execute, register, select, install, elevate, modify `PATH`, or write outside the supplied per-user application-data root.

Expose staging in the local-model window only after the selected runtime archive passes exact catalog review. A separate acknowledgment starts unchecked and unavailable, becomes available only for a reviewed runtime, and is revoked by any candidate or path change. Reauthenticate through the staging service after activation, reset approval after use, move focus to the assertive result, and describe the resulting files as inert rather than installed. Native positive-path automation uses only a generated archive behind an explicit environment-and-root-confined fixture launch; the same launch must fail when that gate is absent.

## Consequences

- Synthetic native-Windows tests can prove approval denial, exact extraction and hashing, source-path minimization, partial-state cleanup, receipt collision denial, immutable-destination behavior, cancellation, and linked-root confinement without downloading a real dependency.
- Native UIA can additionally prove default-off consent, keyboard access, approval revocation on source edits, completion focus, source preservation, receipt minimization, temporary-copy removal, and zero backend registration through the guarded generated-only fixture.
- Staging deliberately incurs a private copy plus two full decompression passes so that extraction never trusts mutable source bytes or a partial structural scan.
- A staged digest tree is not an approved backend. Exact executable/model/license registration, benchmarking, runtime behavior, redistribution review and human accessibility acceptance remain separate gates.
- Real archive transfer and staging remain blocked until the owner explicitly authorizes that bounded local action.
