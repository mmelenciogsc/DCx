# ADR 0059: Protect the exact Whisper stage receipt identity outside the filesystem

- Status: Accepted for per-user Windows filesystem-rollback defense
- Date: 2026-10-18

## Context

ADR 0058 rehashes every staged runtime file and strictly validates its receipt, but an actor able to rewrite both payload bytes and the unsigned receipt could make that filesystem tree internally consistent again. DCx already uses current-user Windows Credential Manager as a separately protected minimum-policy anchor for global-replica rollback defense. A staged runtime needs a similar independent binding before a later registration or benchmark can rely on its receipt.

## Decision

For each catalog-valid Whisper runtime candidate, derive a versioned Credential Manager target from SHA-256 of the candidate identifier and pinned archive digest. Store a bounded schema-v1 anchor containing only the candidate identifier, archive SHA-256, exact receipt SHA-256, exact receipt length and UTC staging time. These values are not credentials or user content, but Credential Manager keeps the trust anchor outside the runtime filesystem and excluded from DCx backup/restore.

Read and parse anchors with strict casing, schema, size, UTC, digest and candidate checks. Reject comments, trailing commas, unknown members, duplicate properties, malformed state and a conflicting second binding. Exact rebinding is idempotent. Cancellation before a read or write creates no credential.

During staging, authenticate and extract the private pinned archive, create the bounded receipt, remove the private archive, then write the protected anchor while the exact receipt is held read-only with write/delete sharing denied. Publish the runtime only after the anchor succeeds. After the same-volume move, immediately run the protected-anchor verifier across the complete tree before reporting success. If an identical inert stage was manually removed, reuse its protected UTC so the same authenticated archive produces the same receipt and can be safely recreated without weakening conflict denial.

The production local-model window uses the Windows store. Generated UIA uses a process-memory fixture anchor behind its existing environment-and-root gates so automation leaves no persistent credential. Separate native tests exercise the real default Windows staging/verifier composition and always delete their generated Credential Manager targets.

The anchor grants no download, installation, registration, selection, execution, provenance, license or redistribution authority. It does not establish publisher authenticity. Windows Credential Manager protects against filesystem-only copying, replacement and rollback; code already executing as the same Windows user may access that user's generic credentials, so process isolation, ACL hardening and reauthentication of the pinned source remain required for a production execution decision.

## Consequences

- Missing, malformed, conflicting or mismatched anchors fail closed before a stage is trusted.
- Replacing both a payload and its filesystem receipt is rejected while the protected anchor remains unchanged.
- Anchor-write failure publishes no runtime. A post-publication race can still leave inert files, but staging never reports success unless the final protected re-verification passes.
- A stale anchor without a stage grants nothing; an exact later recreation is idempotent.
- P06 remains blocked on owner-authorized real dependency transfer, contained-license review, Whisper/embedding quality and resource benchmarks, and human assistive-technology acceptance.
