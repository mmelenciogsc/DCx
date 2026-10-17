# P13 — Packaging, security and reliability

- Status: BLOCKED (substantial local reliability/release-staging checks pass; installer/signing/scanner/clean-VM/human-AT gates absent)
- Implementation commits: `82452fe`, `3e9b172`, `1b32cdf`, `26da605`
- Documentation commit: recorded by the commit containing this file

The generated ZIP is an unsigned framework-dependent staging artifact. It is not an installer or production release.

## Implemented

- Explicit offline-start mode renders a bundled semantic page without contacting a website; native WebView2 UIA exposes its heading and status.
- Quiescent-data backup/restore for browser metadata and private CKB/CCB only. WebView2 profiles/cookies and protected credentials are excluded.
- Backup entries have SHA-256 and length manifests, deterministic ordering/timestamps, size/file-count bounds and atomic final write.
- Restore stages and verifies all entries before replacement, denies traversal/absolute/ADS/reparse paths and archives within the protected root, and restores prior directories if replacement fails.
- The normal app holds an exclusive per-data-root runtime lease. `DCx.exe --recovery` provides a separate keyboard/UIA-accessible backup-and-recovery window; operations fail safely until the main app closes.
- Restore requires a default-unchecked replacement acknowledgment. UI status is an assertive live region, and the main browser exposes an accessible recovery launcher.
- Two framework-dependent Windows publish trees and fixed-timestamp ZIPs are compared by relative path, length and SHA-256.
- CycloneDX 1.6 inventory for the seven shipped NuGet dependencies plus package license metadata.
- Repository-source scan with two exact synthetic hostile-input fixtures allowlisted; every published file receives ASCII binary secret-pattern scanning with no allowlist.
- Authenticode state, startup time and working set are recorded without misrepresenting unsigned output.
- Normal-mode unhandled UI, background-task and terminating-process hooks record only after explicit versioned opt-in and never mark an exception handled.
- Each local crash record has exactly six fields: schema version, UTC time, closed component category, closed error category, terminating flag and application version. Messages, stack traces, dumps, prompts, URLs, paths, keystrokes, accounts and user identifiers are structurally absent.
- Reports remain under the per-user application-data root, reject reparse boundaries, use atomic bounded JSON writes and retain at most 20. There is no network transport.
- A keyboard/UIA-accessible privacy dialog exposes the default-unchecked setting, exact data notice, retained count, explicit purge that preserves the current setting and opt-out that purges reports.
- A strict controlled-update envelope canonically binds product identity, four-part MSIX version, issue/expiry times, HTTPS package URI, exact length/SHA-256, package identity and publisher. ECDSA P-256 verification is mandatory before policy or package retrieval.
- Update admission rejects equal/downgrade versions, manifests issued over five minutes in the future or valid longer than 30 days, expired manifests, cross-origin packages, query/user-info/fragment-bearing URIs, non-MSIX paths and noncanonical/unknown JSON.
- The hardened HTTP transport is HTTPS-only and exact-origin, disables redirects, cookies, decompression and proxies, requires reviewed manifest/package media types and enforces 64 KiB manifest and 1 GiB package ceilings while streaming.
- Package staging requires exact byte length/digest plus an injected signature/identity/publisher/version verifier under the same no-write read lease. Reused files are reverified; roots/reparse points are rejected; only the verified package and exact signed manifest are atomically retained. Nothing invokes installation, elevation or package activation.
- P13/P14 validators now emit TRX and fail unless exact selected counters prove every test passed. P14 uses its exact test namespace instead of a drifting substring match.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p13.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

Recorded evidence directory: `artifacts/test-results/p13-5f3586cd7aef40a7a3e2983d309d51c9/` (generated and intentionally not committed).

- Locked Release build: 0 warnings, 0 errors.
- Reliability tests: passed 23, failed 0, skipped 0; exact counters parsed from the retained TRX.
- Current full suite: passed 228, failed 0, skipped 0; about 9 seconds.
- Publish trees: 42 files, 63,119,779 bytes each; no path/length/SHA-256 differences.
- Reproducible staging ZIP SHA-256: `fb4f2bccb134073a0f2140ccf61e90d22398100fdf83ff6fbf99b8bbfe2730f2` (25,247,628 bytes).
- CycloneDX components: 7; third-party license records: 7.
- Unexpected secret matches: 0; exact synthetic scanner-fixture matches: 2.
- Authenticode: `NotSigned`.
- Offline UIA heading: `DCx is ready offline`; startup 4,180 ms; working set 466.2 MiB under the provisional 1 GiB ceiling.
- Crash-diagnostics UIA heading: `Privacy and crash diagnostics`; consent defaulted unchecked, explicit opt-in persisted notice version 1, purge preserved enabled consent, opt-out disabled and purged, and network transfers remained zero.
- Recovery UIA heading: `DCx backup and recovery`; main-app launcher, path input, create, default-unchecked approval, restore and assertive status are exposed.
- Backup while main DCx held the lease was denied and wrote no archive. After main exit, backup and approved restore passed; restore without approval was denied; excluded synthetic WebView2 cookie data remained unchanged.
- Five update-admission tests passed. They cover successful inert staging/reuse, wrong signing key, equal version, expiry, cross-origin package, malformed/noncanonical envelopes, invalid four-part versions, digest and package-trust failure, post-stage tampering, redirect/origin/media/size transport policy and exact package streaming. All update bytes and trust evidence were synthetic; no external request or installer action occurred.
- Production actions: 0.

## Changed files in the latest milestone

- Canonical signed update policy: `src/DCx.Core/Reliability/ControlledUpdateManifest.cs`.
- Exact-origin bounded HTTP source: `src/DCx.Core/Reliability/ControlledUpdateTransport.cs`.
- Inert digest/trust-gated package staging: `src/DCx.Core/Reliability/ControlledUpdateStager.cs`.
- Denial and reuse coverage: `tests/DCx.Core.Tests/Reliability/ControlledUpdateStagerTests.cs`.
- Exact-counter release gates: `scripts/validate-p13.ps1`, `scripts/validate-p14.ps1`.

## Blockers and limits

- `makeappx.exe`, `signtool.exe`, MSIXMgr and WiX are absent. No code-signing identity is available. Installing tooling or acquiring a certificate requires approval.
- The staging ZIP requires the pinned .NET desktop runtime and WebView2 runtime and provides no install, update activation, uninstall or runtime bootstrap behavior.
- Controlled-update admission is an unwired core boundary. No production update origin/signing-root custody or rotation, real Windows MSIX/Authenticode package verifier, accessible update UI, scheduler, activation, restart coordination, installer integration or rollback exists. The package must be signature/digest/identity reverified again immediately before a future install; admission is not installation authority.
- No supported vulnerability scanner is installed; SBOM/license generation is not a vulnerability result.
- No clean Windows 11 VM installation/uninstallation/upgrade/rollback run occurred.
- Live SQLite/WAL snapshotting is not claimed; the exclusive lease deliberately requires main DCx to be closed during backup and restore.
- No human JAWS, NVDA or Narrator signoff occurred.
- Crash diagnostics deliberately sacrifice message/stack detail for privacy. They have no export, upload, symbolication or support-ingestion path; any future transfer requires a new reviewed notice, item preview and explicit approval boundary.
- Recovery/readiness utility processes do not initialize this store because they intentionally run outside the normal app's exclusive data lease. Their failures are not represented as covered by the current hooks.
- P13 remains BLOCKED until signed MSIX install/update/uninstall on a clean VM, vulnerability and AV scans, WebView2/.NET prerequisite handling and human AT acceptance all pass.
