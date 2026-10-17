# Incident response runbook

This runbook is preparatory. No production service or pilot is active.

## Severity

- SEV-1: credential/private-data exposure, unauthorized publishing/payment/OS action, signing compromise, destructive loss or user-control safety failure.
- SEV-2: sustained outage, reconciliation mismatch, repeated false high-impact claim, inaccessible critical path or corrupt global release with verified containment.
- SEV-3: recoverable defect with no sensitive exposure or external side effect.

## Response sequence

1. Stop the scoped capability. Do not broaden authority, delete evidence or expose private payloads in the incident channel.
2. Preserve bounded logs, stable error codes, signed manifests, checksums and affected versions. Never collect prompts, field values, cookies, API keys or full private files by default.
3. Revoke scoped tokens/keys and disable contribution, payout, updater or provider feature flags when relevant.
4. Roll back to the last verified signed global catalog/package or restore verified quiescent local data. Live-mode financial actions remain disabled.
5. Notify the owner and assigned security/privacy/accessibility/finance role according to severity. External notification requires legal/owner review unless law mandates otherwise.
6. Reproduce with safe fixtures, identify root cause, add denial-path regression coverage and rerun all affected phase gates.
7. Require explicit incident closure and a fresh P14 readiness report before reactivation.

## Evidence and privacy

Record incident ID, time, product version, stable issue codes, affected capability, decisions, hashes and remediation state. Do not record secrets, prompt/output bodies, browser history, file paths, account identifiers or assistive-technology speech content unless a separately approved, minimized collection is essential.

## Exercises still required

Before a pilot, conduct signing-key compromise, malicious global update, private-data exposure, inaccessible takeover, payout duplicate/reconciliation, updater rollback and backup-restore exercises. Assign named on-call contacts and legally reviewed notification timelines.
