# ADR 0058: Reverify inert Whisper stages before later trust decisions

- Status: Accepted for read-only local integrity review
- Date: 2026-10-18

## Context

ADR 0057 authenticates and atomically publishes a Whisper runtime archive as an inert digest-bound tree. A staging receipt is useful only if later code can parse it strictly and prove that the published tree still has the exact files and bytes it recorded. Trusting a receipt without rehashing would allow local mutation, added DLLs, linked content, receipt ambiguity, or candidate substitution between staging and a future registration or benchmark.

## Decision

Provide a read-only `WhisperRuntimeStageVerifier` bound to one catalog-valid Whisper runtime candidate and its exact candidate-plus-archive-digest directory below per-user DCx storage. Require every directory and file in that chain to exist without reparse points. Hold the bounded receipt under a no-write/no-delete lease while validating it.

Parse receipt JSON with comments, trailing commas, unknown members, duplicate properties, unsupported schema versions and non-UTC timestamps denied. Bind the candidate identifier, archive digest and size, catalog license identifier and digest, CLI relative path, counts, reported license paths, total expanded bytes, and each normalized relative path/length/SHA-256 value. Preserve the archive inspector's Windows-safe path and case-collision rules.

Derive the only permitted directory set from the recorded file paths. Enumerate the complete runtime tree, deny reparse points, unexpected or missing directories, unexpected or missing files, and receipt-name collisions. Open every recorded file read-only with write/delete sharing denied, recheck the path, length and SHA-256, and retain those leases until the complete point-in-time review finishes. Return the same bounded staging result shape only after every check passes.

Staging no longer materializes empty ZIP directory entries. It creates only directories required by recorded files, so an exact post-staging tree inventory can reject every extra directory. Archive directory counts remain provenance facts in the receipt; they are not treated as filesystem authorization.

The verifier does not download, install, register, select, execute, delete, modify, or approve any backend. Its result is a point-in-time internal-consistency observation. The receipt has no external signature or separately protected digest, so an actor able to rewrite both the receipt and every affected file is outside this gate; future execution approval must reauthenticate from the pinned archive or add a protected trust anchor. Any later execution boundary must also revalidate and lease the exact executable, model and license files it consumes. Same-user filesystem races still require production ACL and process-isolation controls.

## Consequences

- Native generated-only tests prove exact re-verification and rejection of changed file bytes, extra files, extra directories, linked content, unknown and duplicate receipt fields, and mismatched receipt identity.
- A future registration or benchmark can use this check to reject stale or internally inconsistent stage state, but cannot treat it as publisher authenticity or an execution authorization.
- No real dependency is transferred or granted execution authority by this decision.
- P06 remains blocked until explicitly authorized real Whisper and embedding candidates pass provenance, license, quality, resource, unload and accessibility gates.
