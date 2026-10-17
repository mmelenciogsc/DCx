# P10 — Contributions, governance and provenance

- Status: BLOCKED (CKB local governance gate passes; untrusted CCB sandbox unavailable)
- Partial implementation commit: `4016c2c`
- Documentation commit: recorded by the commit containing this file

No contribution was sent externally or published to a production catalog.

## Implemented

- Accessible Windows contribution-review surface with global preparation opt-in initially unchecked, three unchecked attestations, unchecked digest-bound item approval, disabled preparation action and explicit no-upload status.
- Editing the item clears review and approval. Prepared items always begin in local quarantine; the UI contains no upload/publish action.
- Canonical item digest covers body digest, resource identity/version, source, license, contributor pseudonym, artifact digest, capabilities and derivative credits.
- Client and authoritative rescans for common private keys/API credentials, email/phone/government ID, suspicious shell patterns, dummy/empty material, size, license and artifact-policy findings. Finding records contain codes, never matched values.
- Ownership/share-rights, license and sensitive-data attestations plus a 30-minute, nonce-bearing, exact-item approval.
- SQLite quarantine with unique approval nonces, idempotent submission identity, reviewer transition, and CKB propagation into the signed P09 release chain only after a clean authoritative rescan.
- Withdrawal publishes a signed deprecation.
- Derivative credit graph rejects duplicate/invalid weights, allocation above 100% and cycles.
- Minimal usage-receipt contract excludes prompts, bodies, paths and URLs; HMAC integrity, receipt/nonce replay denial, entitlement/global-resource checks, self-use exclusion, success evidence, time window and daily anomaly cap.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked native Release build: 0 warnings, 0 errors; 1.10 seconds in the recorded dedicated run.
- P10 tests: passed 8, failed 0, skipped 0; 397 ms.
- Full suite: passed 94, failed 0, skipped 0; about 1 second.
- Eligible CKB item: quarantined, absent from catalog before review, approved into a signed release, synchronized by a client, then withdrawn/deprecated.
- Secret/PII/dummy item: remained quarantined and reviewer publication failed closed.
- Receipt: one qualified event; replay, contributor self-use and invalid integrity excluded.
- Native UIA: all five consent/attestation controls initially unchecked, preparation disabled, and no-upload state accessible.

## Blocker and limits

- CCB contributions are deliberately denied with `ccb_sandbox_review_required`. No secure untrusted-code build/execute sandbox is available; Docker/Compose is blocked as recorded in P09, and ordinary Windows child-process restriction is not a sufficient hostile-code sandbox. P10 is therefore not complete.
- After an approved sandbox is available, add dependency/lock/SBOM/license scans, build/test network denial, filesystem/process budgets and malicious-package fixtures before enabling CCB reviewer approval.
- Regex scanners are defense-in-depth, not a guarantee that content lacks secrets/PII or that the contributor owns it. Human/policy review remains mandatory.
- An HMAC proves receipt integrity/installation association, not that an untrusted customer device reported honest usage. Server anomaly analysis, audit sampling and entitlement/account correlation remain required.
- The simulation publishes to the registry and then updates governance state across separate SQLite databases. Production requires an idempotent transactional outbox/reconciliation design.
- Human JAWS/NVDA/Narrator testing remains unverified; see `docs/accessibility/P10_CONTRIBUTION_CHECKLIST.md`.
