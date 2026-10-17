# ADR 0010: Closed-app recovery under an exclusive data lease

- Status: Accepted
- Date: 2026-10-17

## Context

Copying live SQLite databases and WAL files does not produce a reliable general backup. Restoring browser metadata or private knowledge while DCx is using it risks corruption. A recovery tool must remain available when the main browser is closed and must expose native accessibility semantics without creating a second packaging identity.

## Decision

The normal app acquires an exclusive file lease scoped to the canonical DCx data root for its lifetime. The same installed executable supports a separate `--recovery` mode. Recovery acquires that lease only for the complete backup or restore operation, so the window may explain a conflict while main DCx is open but cannot touch protected data until it closes.

Recovery exposes native labeled controls and an assertive status live region. Restore additionally requires a default-unchecked acknowledgment that current browser metadata and private knowledge will be replaced. The existing bounded manifest, staging, digest verification and rollback service remains the only component that reads or replaces backup payloads.

## Consequences

- Backup and restore have race-free closed-app orchestration without a privileged service or live database snapshot.
- One signed application identity can launch both browser and recovery experiences.
- A crash releases the operating-system file handle; a stale marker file carries no authority.
- The workflow does not provide live backup. Installer integration and human JAWS/NVDA/Narrator acceptance remain required.
