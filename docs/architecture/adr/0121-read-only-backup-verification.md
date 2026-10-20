# ADR 0121: Verify backups without lease or recovery authority

- Status: Accepted for local recovery hardening; production retention and lifecycle policy remain blocked.
- Date: 2026-10-20
- Source: `7ff6387`

## Decision

Recovery exposes a read-only **Verify selected backup** action (`Alt+V`). It uses the same strict archive-admission verifier as restore: bounded ZIP entries, one strict manifest, duplicate-property rejection, safe relative paths, exact file count/length and SHA-256 checks, and exact manifest/payload-set equality. Verification writes no staging files, changes no application data, and acquires no application-data lease, so it remains safe while normal DCx owns the lease.

The verifier is deliberately separate from restore authority. A successful verification never enables **Return to normal DCx**, never creates a backup, never replaces data and never invokes an installer, PackageManager or external process. Restore still requires the exclusive lease and its separate default-off approval.

## Rationale and limits

Sharing one admission implementation prevents verification and restore from drifting. Strict JSON and duplicate ZIP/path checks close ambiguity and overwrite risks. Read-only operation supports preflight inspection while the app is running, but it does not attest that a backup is suitable for disaster recovery, encrypted at rest, retained under policy or restorable on a clean machine. Human JAWS/NVDA/Narrator acceptance and production recovery policy remain required.

## Evidence

The exact native P13 gate proves UIA access key `V`, successful verification while the live lease is held, unchanged archive and original data metadata/bytes, Return authority false and zero production actions. Focused hostile tests cover duplicate JSON fields, unknown fields and duplicate payload paths.
