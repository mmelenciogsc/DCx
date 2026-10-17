# ADR 0006: Synchronize the global replica as a signed delta chain

- Status: Accepted
- Date: 2026-10-17

## Decision

The authoritative service publishes monotonically sequenced canonical delta manifests signed with a pinned ECDSA P-256 trust root. Every manifest binds the previous signed-release digest and the resulting canonical catalog digest. Clients download bounded chunks to a stable partial file, verify envelope checksum, signature, chain link and resulting catalog, then atomically replace a small active pointer. Prior verified generations remain available for explicit rollback.

Catalog metadata synchronizes eagerly. Signed artifact digest/size/media metadata synchronizes with it, but artifact bytes download only on use and receive an independent final digest check. A local global replica lives beneath `global/<trust-root-hash>` and has no code path into writable private-profile roots.

## Consequences

- A corrupt transfer, invalid signature, broken chain or modified result cannot activate.
- Interrupted transfers resume from the durable partial-file length; an active catalog is never modified in place.
- The server can publish deprecations and removals while clients retain previously verified rollback generations.
- A same-Windows-user attacker can point at an older locally retained valid generation; hardware-backed monotonic rollback resistance is not claimed.
- Production signing-key custody, rotation, revocation, HTTPS deployment and disaster recovery remain deployment gates. The P09 key and loopback service are simulation fixtures only.
