# P09 — Global registry and synchronization

- Status: BLOCKED (native process/HTTP simulation passes; required Docker Compose execution unavailable)
- Partial implementation commits: `1be3f9e`, `b1608cd`, `9123a81`
- Documentation commit: recorded by the commit containing this file

No connection or deployment to the owner's Ubuntu server was attempted.

## Implemented

- Linux-capable ASP.NET Core service with an authoritative SQLite release/artifact database and local-simulation-only startup gate.
- Canonical ECDSA P-256 signed release manifests binding sequence, previous release checksum, changes and resulting catalog checksum.
- Upsert, deprecation and removal reducer with duplicate/version/conflict denial.
- Content-addressed authoritative artifacts; a release cannot reference absent or conflicting artifact metadata.
- Strict HTTPS remote transport policy with loopback HTTP allowed only for local simulation; bounded descriptor and byte-range endpoints.
- Per-trust-root global client namespace physically separate from every P08 private profile namespace.
- Checkpointed bounded release/artifact chunks, checksum and signature verification, monotonic chain checks, canonical result verification and atomic active-pointer replacement.
- Locally searchable offline catalog with optional vectors, explicit rollback to retained verified generations and lazy artifact download.
- Canonical offline-root-signed trust policies with SHA-256 public-key identities, per-sequence validity, effective-sequence revocation and a bounded 64-key policy.
- Durable authoritative trust-policy history and bounded HTTP descriptor/chunk endpoints; clients catch up every missed revision and verify the complete hash-linked policy chain before releases.
- Policy replay/conflict and unauthorized old-key releases fail closed. Rollback candidates are fully verified under the current policy before the active pointer changes.
- Read-only hardened Compose definition, non-root container, loopback host port, no-new-privileges, excluded secrets/build artifacts and externally supplied signing key.
- Atomic publication-operation records bind a stable operation ID to the canonical ordered change-set digest and signed release sequence. An identical retry after repository reopen returns the original descriptor; reuse with changed content fails closed.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p09.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked restore and native Release build passed with 0 warnings/errors.
- P09 filtered tests: passed 9, failed 0, skipped 0; about 1 second in the recorded validation.
- Current full suite after P10 durable simulation-worker hardening: passed 182, failed 0, skipped 0; about 7 seconds.
- A real child Kestrel process served three chained trust-policy revisions and a signed SQLite-backed release over loopback HTTP; a fresh production client transport caught up all revisions, synchronized and searched it.
- Two distinct client roots synchronized the same chain; the P08 private sentinel remained present and absent from global results.
- Wrong-key release activation was rejected with no active pointer.
- A root-signed rotation revoked the old release key from sequence 2. An old-key sequence-2 release, an attacker-signed policy and a policy replay were rejected; the authorized replacement key succeeded.
- A rollback to a generation invalidated by an emergency revocation was rejected before pointer replacement, leaving the active sequence usable across reopen.
- A transfer interrupted after its first 64 KiB chunk resumed at a nonzero offset and downloaded fewer bytes on retry than the full release.
- Lazy 150,000-byte artifact retrieval, deprecation, offline search and explicit rollback passed.
- Publication retry used reordered equivalent changes and a later timestamp after repository reopen, returned the byte-identical original descriptor and kept the chain at sequence 1; a changed request under the same operation ID was rejected.
- Latest storage evidence: `artifacts/test-results/p09-4f008b96a4294306a546f731a085644a/storage-forecast.json` (generated and intentionally not committed).

## Measured storage/bandwidth fixture

The deterministic benchmark uses 1,000 resources and excludes artifact bodies:

| Fixture | Bytes |
|---|---:|
| Metadata-only canonical catalog | 470,703 |
| Initial signed release | 722,689 |
| Catalog with 384-dimensional vectors | 4,388,562 |
| Signed delta changing 100 resources | 73,554 |
| Two retained metadata catalogs plus two releases | 1,738,549 |

Artifact storage and transfer must add exact signed artifact sizes. Artifacts are not included in catalog sync and download only on use.

## Blocker and limits

- Native Windows validation has no Docker CLI. WSL reports Docker 29.1.3, but daemon access is permission-denied and no Compose plugin or `docker-compose` command exists. Installing/enabling these is privileged and requires owner approval.
- Consequently, `server/docker-compose.simulation.yml` is unexecuted and P09 is not complete. After approval/access, validate Compose configuration, build with recorded image digests, run the same two-client gate against the container, then update this phase.
- Simulation signing keys and the offline root are ephemeral fixture material. The rotation protocol is implemented, but production offline-root custody/ceremony, hardware- or OS-protected minimum-revision anchoring, TLS identity, backup/restore, monitoring and external security review are not implemented.
- The service exposes read-only catalog endpoints by default. P10 adds a separate default-off, loopback/container simulation contribution API; this is not authorization or infrastructure for production exposure.
- Real Ubuntu deployment remains forbidden without explicit owner approval.
