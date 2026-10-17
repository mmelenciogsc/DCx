# ADR 0053: Confine global replica and authoritative registry storage paths

- Status: Accepted for local and simulation storage; privileged deployment remains blocked
- Date: 2026-10-18

## Context

P09 cryptographically authenticates policies, releases, catalogs and artifacts, but signature validity does not make a filesystem destination safe. A pre-existing ancestor link, a swapped staging/artifact directory, a linked pointer/database file or tampered artifact-relative path could redirect reads or writes outside the intended replica or authoritative registry root.

## Decision

Both client and service use one shared path guard. Application-data, replica, release, catalog, staging, artifact, authoritative registry and digest directories must be non-root local canonical paths whose existing ancestor chain contains no reparse point. Every operation rechecks its required directory layout. Existing and proposed pointer, trust-policy, checkpoint, generation, artifact, SQLite, WAL and shared-memory paths must be regular non-reparse files beneath a currently safe parent.

The authoritative artifact database path must equal the digest-derived `artifacts/sha256/<prefix>/<digest>` path exactly before publication or range service. Arbitrary contained relative paths are not accepted. The service signing-key input receives the same regular-file and ancestor admission before its bounded read lease. Registry keys must be one exact P-256 PKCS#8 key.

Guard failures surface stable category messages without including the rejected local path. The policy does not delete or repair linked inputs and grants no installation, deployment or elevation authority.

## Consequences

- Native hostile fixtures deny filesystem roots, linked application/server ancestors, swapped staging/artifact directories, linked active-pointer/database files, artifact-path metadata substitution and non-P-256 keys without writing through the links.
- Normal two-client HTTP synchronization, policy rotation, checkpoint resume, lazy artifacts and publication replay continue to pass.
- Check-then-open races remain possible against malicious code running as the same OS user. Production still needs least-privilege service accounts, directory ACLs/mount policy, deployment isolation, backup/restore and external security review.
- This hardening does not provide hardware-backed monotonicity or execute the blocked Docker Compose gate.

## Alternatives rejected

- Rely only on content signatures: does not prevent an authenticated object from being written to an attacker-selected path.
- Check only the final root at startup: misses linked ancestors and post-open directory substitution.
- Accept any artifact path that merely remains under the server root: unnecessary flexibility permits metadata redirection away from the digest-derived store.
