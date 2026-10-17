# ADR 0005: Use embedded SQLite for the private CKB/CCB index

- Status: Accepted
- Date: 2026-10-17

## Context

The private store must install with the Windows client, work offline, remain physically separate by local profile, and not require WSL or a customer-managed database daemon. P08 allows Qdrant or a selected embedded vector index alongside SQLite metadata.

## Decision

Use pinned `Microsoft.Data.Sqlite` with bundled SQLite for transactional metadata, version/dependency history and FTS5 sparse retrieval. Store bounded float vectors as SQLite BLOBs and calculate cosine similarity in managed code over at most 5,000 current private resources. Combine reciprocal FTS rank and normalized cosine score for hybrid retrieval. Keep content-addressed artifacts in a sibling SHA-256 directory within each hashed profile namespace.

Do not install Qdrant on the Windows client. Do not load code directly from a CCB package. Only a host-registered adapter whose resource ID, version and package digest match passed signed metadata and a bounded reproducible test harness can enter BOSS as `VettedCcb`.

## Consequences

- Offline install and backup are simple and transactional; no customer service daemon or WSL dependency is introduced.
- Private profile databases and artifacts are physically separate from one another and from the future read-only global replica.
- The bounded managed vector scan is appropriate for the initial private catalog, not a claim of large-catalog vector performance. Measure before raising the cap or adopting a pinned SQLite vector extension.
- The P09 authoritative server may choose a different vector engine after its own measured gate. Its replica format must not weaken the private/global separation.
- LocalAppData ACL inheritance provides the current at-rest boundary; database encryption and recovery-key design remain future security decisions rather than implied capabilities.
