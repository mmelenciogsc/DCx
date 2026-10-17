# P09 — Global registry and synchronization

- Status: In progress (local simulation only)
- Commands/results: Docker CLI present; daemon access denied in P00
- Changed files: none
- Commit: none

Build a local simulation first. No real Ubuntu-server connection or deployment is authorized.

Active work: implement the authoritative catalog/release service, pinned signing trust, checkpointed client download, corrupt/interrupted-update recovery, atomic replica activation, rollback and two-client/private-isolation tests without contacting the owner's server.
