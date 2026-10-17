# P08 — Private local CKB/CCB

- Status: Complete (automated local/native gate)
- Implementation commit: `685ca5a`
- Documentation commit: recorded by the commit containing this file

## Implemented

- Per-profile private namespaces derived from a SHA-256 profile identifier under the per-Windows-user application-data root. Each namespace owns a separate SQLite database and artifact tree.
- Pinned `Microsoft.Data.Sqlite` 10.0.13 with locked transitive dependencies and hashes; SQLite transactions use foreign keys, full synchronization, WAL and a bounded busy timeout.
- Immutable resource versions, optimistic head updates, dependency/capability tables, FTS5 sparse index and bounded float-vector storage.
- Offline hybrid retrieval combines reciprocal FTS rank and normalized managed cosine similarity over at most 5,000 current resources.
- SHA-256 content-addressed artifacts with atomic writes, size/digest checks and integrity verification on CCB reads.
- CCB ZIP validation for entry count, expansion, path traversal, duplicate paths, symbolic links, manifest size/schema and declared entrypoint.
- Pinned ECDSA P-256 publisher trust keys and canonical signed provenance covering resource ID/version, title, body digest, source, license, package digest, capabilities and dependencies.
- Package manifest ID/version cross-check and signature/digest revalidation before CCB retrieval.
- Bounded reproducible registered-tool harness with exact output hashes, timeout/input/output limits and cancellation.
- `VettedCcbCapability` enters BOSS only after tool ID, version and artifact digest match both the stored signed resource and passing harness evidence.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p08.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked restore: passed.
- Native Release build: 0 warnings, 0 errors; 1.05 seconds in the dedicated run.
- P08 knowledge tests: passed 5, failed 0, skipped 0; 362 ms.
- Full suite after implementation: passed 79, failed 0, skipped 0; 911 ms.
- Dedicated validator reported PASS for profile isolation, offline FTS5/vector retrieval, version conflict, content-addressed integrity, hostile ZIP rejection, signed provenance/tamper detection, bounded harness and BOSS reuse.

## Changed areas

- `src/DCx.Core/Knowledge` — store, artifacts, package/provenance policy, harness and BOSS adapter.
- `tests/DCx.Core.Tests/Knowledge` — deterministic gate fixtures.
- `scripts/validate-p08.ps1` — locked native reproduction.
- `Directory.Packages.props` and project lock files — exact embedded-provider dependency graph.

## Known limits

- P08 consumes caller-provided validated vectors. Local embedding generation remains part of blocked P06 and is not claimed.
- Downloaded CCB package code is never automatically loaded or executed. P08 reuses only a host-registered adapter after provenance and fixture gates; a future sandbox must pass before untrusted package execution is considered.
- Managed cosine search is deliberately capped at 5,000 current private resources and has no large-catalog performance claim.
- LocalAppData ACL inheritance is the current confidentiality boundary. The SQLite file is not encrypted at rest, and another process running as the same Windows user is inside this local trust boundary.
- Global trust roots, signed replica update/rollback and lazy global artifacts belong to P09 and remain separate from this writable private store.
