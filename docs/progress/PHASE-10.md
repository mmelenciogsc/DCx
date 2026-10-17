# P10 — Contributions, governance and provenance

- Status: BLOCKED (local server-side CCB orchestration and hostile-code policy pass; production publication remains unavailable)
- Partial implementation commits: `4016c2c`, `ce8d4de`, `adbb511`, `00fedd6`, `9123a81`
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
- Contribution CCB schema v2 is an exact five-file profile: manifest, BusyBox-shell entrypoint, deterministic tests, CycloneDX SBOM and license. Unknown manifest/test fields, duplicate JSON properties, undeclared ZIP members, path aliases/collisions, non-UTF-8 or null-bearing text and unclosed runtime dependencies fail admission. Existing P08 schema v1 remains compatible.
- Candidate identity/version/license are cross-checked against the package and SBOM. All package text and test inputs receive authoritative secret/PII/suspicious-command scanning; scan storage contains codes only.
- Accepted ZIPs enter a digest-addressed quarantine. Approval rereads and rehashes the exact artifact, safely extracts only admitted files into an ephemeral workspace, recomputes its canonical digest and sends every declared test to the injected server-side sandbox executor.
- Each result must cryptographically bind policy, artifact, workspace, test suite, launcher, backend, runtime and output. Minimized evidence hashes/codes are persisted without request or output bodies. A failed test, output mismatch, changed package, forged binding or missing executor prevents publication.
- Only after every test passes is the original digest-verified ZIP added to the P09 artifact store and referenced by a signed global release. A replica test verifies signed synchronization and lazy byte-identical artifact retrieval.
- Approval and withdrawal use digest-derived stable publication-operation IDs. The registry atomically binds each ID to the canonical change set and release; a retry after the registry commit but before governance-state update returns the original release instead of appending a duplicate.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p10.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\ci.ps1
```

## Measured result

- Locked native Release build: 0 warnings, 0 errors; 1.23 seconds in the recorded dedicated run.
- P10 tests: passed 19, failed 0, skipped 0; about 1 second in the recorded native gate.
- Full suite: passed 163, failed 0, skipped 0; about 1 second.
- Eligible CKB item: quarantined, absent from catalog before review, approved into a signed release, synchronized by a client, then withdrawn/deprecated.
- Secret/PII/dummy item: remained quarantined and reviewer publication failed closed.
- Receipt: one qualified event; replay, contributor self-use and invalid integrity excluded.
- Native UIA: all five consent/attestation controls initially unchecked, preparation disabled, and no-upload state accessible.
- WSL-hosted Ubuntu local simulation: Bubblewrap 0.6.1 SHA-256 `bf6cf3d4456665f5d80c14eb79a76e0ce87b39a0883e614a161658b4ec33492c`; static BusyBox SHA-256 `b3c1009e1b5c927e537487c80639cdf404f69e3eb49371d9be5d841672be3ff9`.
- Benign transformation passed. Host/repository and `/etc`/`/usr` reads, an external network request, root write, inherited secret environment, outside-quarantine workspace, symlink, workspace-digest substitution, timeout and output overflow were denied.
- Evidence: `artifacts/test-results/p10-dd1f4845e5854d1bb060280d6a66ffa0/sandbox-evidence.json` (generated and intentionally not committed).
- Latest orchestration-era hostile evidence: `artifacts/test-results/p10-8a9a316263d34dfc8c5f9f325bb2ceee/sandbox-evidence.json` (generated and intentionally not committed).
- Schema-v2 success path executed two declared tests, persisted two passing evidence rows, published the exact artifact into a signed release, synchronized it to a separate global replica and lazily retrieved byte-identical content.
- Denial coverage includes missing executor, package/digest/metadata mismatch, undeclared files/fields, invalid UTF-8/base64/null input, malicious entrypoint or test-input text, expected-output mismatch and post-intake quarantine tampering.
- Cross-database crash simulations pre-commit approval and withdrawal releases, then verify governance retries reuse sequences 1 and 2 and synchronize one correctly deprecated resource rather than producing duplicate releases.

## Changed files in the latest milestone

- Contracts/evidence: `src/DCx.Core/Contributions/CcbSandboxContracts.cs`.
- Linux executor/policy: `server/DCx.GlobalService/BubblewrapCcbSandboxExecutor.cs`, `server/sandbox/run-ccb-bubblewrap.sh`.
- Hostile fixtures and gates: `tests/DCx.Core.Tests/Contributions/CcbSandboxEvidenceTests.cs`, `tests/fixtures/ccb-sandbox/`, `scripts/validate-p10-sandbox.sh`, `scripts/validate-p10.ps1`.
- Package and publication boundary: `src/DCx.Core/Knowledge/CcbPackageValidator.cs`, `server/DCx.GlobalService/ContributionGovernanceService.cs`.
- Orchestration and denial tests: `tests/DCx.Core.Tests/Contributions/CcbContributionPackageTests.cs`, `tests/DCx.Core.Tests/Contributions/ContributionGovernanceTests.cs`.

## Blocker and limits

- The local server fixture now exercises package intake through signed propagation, but `Program.cs` exposes only read-only registry endpoints: no contribution upload, authentication, reviewer or approval API is registered. No external caller can activate this path.
- The current runtime profile accepts only exact, bounded BusyBox-shell packages with a two-component closed SBOM. It has no language-specific reproducible builder images or general dependency-lock resolver.
- This local WSL run is development evidence, not execution on the owner's Ubuntu 24.04 server. WSL is not and will not be an end-user requirement.
- The prototype has no seccomp syscall profile, cgroup-v2 enforcement, immutable OCI/base-image digest, service-account deployment hardening, kernel/LTS policy, production monitoring or independent penetration test. Bubblewrap itself explicitly requires the caller to define a complete policy; this milestone does not claim production-grade hostile isolation.
- Docker/Compose remains unavailable as recorded in P09. Production CCB publication must remain unavailable until approved Ubuntu 24.04 deployment, authenticated intake/reviewer APIs, the remaining isolation controls and external security gates pass.
- Regex scanners are defense-in-depth, not a guarantee that content lacks secrets/PII or that the contributor owns it. Human/policy review remains mandatory.
- An HMAC proves receipt integrity/installation association, not that an untrusted customer device reported honest usage. Server anomaly analysis, audit sampling and entitlement/account correlation remain required.
- The registry release and operation record are atomic, and explicit governance retries safely reconcile the remaining cross-database state update. No automatic recovery worker currently enumerates and retries stalled quarantine/approval rows after a process crash.
- The current approval lock is held while tests execute, serializing governance mutations; production requires a durable queued worker/lease model before concurrent hostile workloads.
- Human JAWS/NVDA/Narrator testing remains unverified; see `docs/accessibility/P10_CONTRIBUTION_CHECKLIST.md`.
