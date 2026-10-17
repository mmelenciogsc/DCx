# ADR 0024: Anchor the minimum global trust-policy revision outside replica files

- Status: Accepted for per-user Windows file-rollback defense
- Date: 2026-10-17

## Context

The offline-root-signed trust-policy chain can revoke a compromised release key, but the active policy previously lived only beside the global replica. Restoring an older copy of that directory could restore an older valid root-signed policy and erase the revocation. The existing explicit release rollback feature must remain available when the target release still validates under the current policy.

## Decision

The Windows client stores a strict schema-v1 anchor in Windows Credential Manager under a target isolated by the offline trust-root SHA-256. The anchor contains only the trust-root digest, highest accepted policy revision and exact policy-envelope digest. It cannot move backward or accept a different digest at the same revision.

Replica open and every policy read compare the root-signed local policy to this protected anchor. A missing local policy behind an anchor, lower revision, same-revision conflict or jump without the anchored predecessor fails closed. Policy update commits the signed file first and advances the anchor second. If interruption occurs in that window, the next open may recover exactly one higher revision only when its signed predecessor digest equals the anchor.

The anchor applies to trust-policy revisions, not release sequences. Explicit rollback to an older retained release remains possible only after that release is verified under the current anchored policy.

## Consequences

- Restoring only older replica files cannot silently remove a recorded revocation.
- Trust roots have independent Credential Manager targets.
- Existing valid installations with no anchor seed it from their current root-signed policy during migration.
- Global replica backup remains excluded; Credential Manager state is not copied into DCx archives.
- This is per-user OS protection, not hardware-backed monotonic storage. A process acting as the same Windows user can delete the credential; deleting both anchor and replica state defeats history detection. Production still needs root-key custody, recovery policy and assessment of TPM or service-backed transparency/monotonicity.

## Alternatives rejected

- Store the floor inside the replica: the same filesystem rollback restores both policy and floor.
- Anchor the release sequence: that would disable the required verified release rollback feature.
- Advance the anchor before the policy file: interruption would leave an anchor ahead of all locally recoverable signed state.
- Accept an arbitrary multi-revision file jump: the client retains only the current policy file, so it cannot prove all omitted predecessor transitions locally.
