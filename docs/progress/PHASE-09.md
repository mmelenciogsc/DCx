# P09 — Global registry and synchronization

- Status: BLOCKED (native process/HTTP simulation passes; required Docker Compose execution unavailable)
- Partial implementation commit: `1be3f9e`
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
- Read-only hardened Compose definition, non-root container, loopback host port, no-new-privileges, excluded secrets/build artifacts and externally supplied signing key.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p09.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked restore and native Release build passed with 0 warnings/errors.
- P09 filtered tests: passed 7, failed 0, skipped 0; 833 ms in the recorded validation.
- Full suite: passed 86, failed 0, skipped 0; about 1 second.
- A real child Kestrel process served a signed SQLite-backed release over loopback HTTP; the production client transport synchronized and searched it.
- Two distinct client roots synchronized the same chain; the P08 private sentinel remained present and absent from global results.
- Wrong-key release activation was rejected with no active pointer.
- A transfer interrupted after its first 64 KiB chunk resumed at a nonzero offset and downloaded fewer bytes on retry than the full release.
- Lazy 150,000-byte artifact retrieval, deprecation, offline search and explicit rollback passed.

## Measured storage/bandwidth fixture

The deterministic benchmark uses 1,000 resources and excludes artifact bodies:

| Fixture | Bytes |
|---|---:|
| Metadata-only canonical catalog | 470,703 |
| Initial signed release | 722,674 |
| Catalog with 384-dimensional vectors | 4,388,562 |
| Signed delta changing 100 resources | 73,554 |
| Two retained metadata catalogs plus two releases | 1,738,534 |

Artifact storage and transfer must add exact signed artifact sizes. Artifacts are not included in catalog sync and download only on use.

## Blocker and limits

- Native Windows validation has no Docker CLI. WSL reports Docker 29.1.3, but daemon access is permission-denied and no Compose plugin or `docker-compose` command exists. Installing/enabling these is privileged and requires owner approval.
- Consequently, `server/docker-compose.simulation.yml` is unexecuted and P09 is not complete. After approval/access, validate Compose configuration, build with recorded image digests, run the same two-client gate against the container, then update this phase.
- Simulation signing keys are ephemeral fixture files. Production key custody, rotation/revocation, TLS identity, backup/restore, monitoring and external security review are not implemented.
- The service currently exposes read-only public catalog endpoints. Contribution intake/authentication begins in P10; production exposure is forbidden.
- Real Ubuntu deployment remains forbidden without explicit owner approval.
