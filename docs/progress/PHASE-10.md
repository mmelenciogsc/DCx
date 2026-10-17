# P10 — Contributions, governance and provenance

- Status: BLOCKED (CKB governance and a rootless hostile-code prototype pass; authoritative CCB publication remains disabled)
- Partial implementation commits: `4016c2c`, `ce8d4de`, `adbb511`
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
- Server-side Linux sandbox contract and versioned evidence bind artifact, canonical extracted workspace, test suite, policy launcher, Bubblewrap backend, static runtime and output digests.
- Rootless Bubblewrap policy creates new user, mount, PID, network, IPC, UTS and cgroup namespaces; drops every capability; clears the environment; uses UID/GID 65534; and exposes only a read-only package, read-only request, pinned static BusyBox, minimal `/proc`/`/dev`, and private tmpfs work areas.
- The empty root is remounted read-only. No host home, repository, system tree, socket, D-Bus endpoint or external network route is mounted.
- Canonical workspace admission rejects traversal, unsupported names, links/special files, more than 256 entries, more than 64 MiB, digest substitution and requests over 1 MiB.
- CPU, address-space, process, descriptor, output-file, diagnostic, wall-clock and captured-output bounds fail closed. Child diagnostics are never retained.
- The production executor rehashes Bash, the policy launcher, Bubblewrap and runtime before every execution, uses argument arrays and a cleared environment, kills the process tree on cancellation, and removes per-operation input storage.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked native Release build: 0 warnings, 0 errors; 1.23 seconds in the recorded dedicated run.
- P10 tests: passed 12, failed 0, skipped 0; 436 ms.
- Full suite: passed 155, failed 0, skipped 0; about 1 second.
- Eligible CKB item: quarantined, absent from catalog before review, approved into a signed release, synchronized by a client, then withdrawn/deprecated.
- Secret/PII/dummy item: remained quarantined and reviewer publication failed closed.
- Receipt: one qualified event; replay, contributor self-use and invalid integrity excluded.
- Native UIA: all five consent/attestation controls initially unchecked, preparation disabled, and no-upload state accessible.
- WSL-hosted Ubuntu local simulation: Bubblewrap 0.6.1 SHA-256 `bf6cf3d4456665f5d80c14eb79a76e0ce87b39a0883e614a161658b4ec33492c`; static BusyBox SHA-256 `b3c1009e1b5c927e537487c80639cdf404f69e3eb49371d9be5d841672be3ff9`.
- Benign transformation passed. Host/repository and `/etc`/`/usr` reads, an external network request, root write, inherited secret environment, outside-quarantine workspace, symlink, workspace-digest substitution, timeout and output overflow were denied.
- Evidence: `artifacts/test-results/p10-dd1f4845e5854d1bb060280d6a66ffa0/sandbox-evidence.json` (generated and intentionally not committed).

## Changed files in the latest milestone

- Contracts/evidence: `src/DCx.Core/Contributions/CcbSandboxContracts.cs`.
- Linux executor/policy: `server/DCx.GlobalService/BubblewrapCcbSandboxExecutor.cs`, `server/sandbox/run-ccb-bubblewrap.sh`.
- Hostile fixtures and gates: `tests/DCx.Core.Tests/Contributions/CcbSandboxEvidenceTests.cs`, `tests/fixtures/ccb-sandbox/`, `scripts/validate-p10-sandbox.sh`, `scripts/validate-p10.ps1`.

## Blocker and limits

- CCB contributions remain deliberately denied with `ccb_sandbox_review_required`. The namespace policy and executor exist, but authoritative artifact intake/extraction, dependency/lock/SBOM/license validation, multi-test orchestration, durable evidence, reviewer transition and signed artifact publication are not connected.
- The current runtime profile accepts only bounded BusyBox shell fixtures. It has no language-specific reproducible builder images.
- This local WSL run is development evidence, not execution on the owner's Ubuntu 24.04 server. WSL is not and will not be an end-user requirement.
- The prototype has no seccomp syscall profile, cgroup-v2 enforcement, immutable OCI/base-image digest, service-account deployment hardening, kernel/LTS policy, production monitoring or independent penetration test. Bubblewrap itself explicitly requires the caller to define a complete policy; this milestone does not claim production-grade hostile isolation.
- Docker/Compose remains unavailable as recorded in P09. CCB publication must remain disabled until the authoritative end-to-end path and external security gate pass.
- Regex scanners are defense-in-depth, not a guarantee that content lacks secrets/PII or that the contributor owns it. Human/policy review remains mandatory.
- An HMAC proves receipt integrity/installation association, not that an untrusted customer device reported honest usage. Server anomaly analysis, audit sampling and entitlement/account correlation remain required.
- The simulation publishes to the registry and then updates governance state across separate SQLite databases. Production requires an idempotent transactional outbox/reconciliation design.
- Human JAWS/NVDA/Narrator testing remains unverified; see `docs/accessibility/P10_CONTRIBUTION_CHECKLIST.md`.
