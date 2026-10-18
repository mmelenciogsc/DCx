# P13 — Packaging, security and reliability

- Status: BLOCKED (substantial local reliability, self-contained unsigned-MSIX staging, Defender and dependency-advisory checks pass; signed installer, clean-VM, independent security and human-AT gates remain absent)
- Implementation commits: `82452fe`, `3e9b172`, `1b32cdf`, `26da605`, `7b0a03b`, `a69f5bf`, `e20a7d3`, `993149e`, `54f66bc`, `acc8e62`, `bcfe167`, `8b94159`, `9877a60`, `faf6b71`, `1631ebb`, `599cee9`, `ec37d1f`, `a662abb`, `8583b29`, `d5624a6`, `9576d63`, `261f6ee`, `76d0750`, `562667b`, `f66dd20`, `3bf9452`, `23ed1d9`, `9140183`, `8cb2db4`, `eb81ac3`, `66c4c8c`, `05345b9`, `0c72488`, `70f8dd7`, `abd91c0`, `7829af1`, `8783ed2`, `7df8c0a`, `84de38b`, `1bb4d7e`, `efc607c`, `0381f5e`, `b7d281e`, `21d8a9d`, `89f7bb3`, `6bd7acc`, `e559975`, `c292a21`, `f4810e0`, `0518333`, `040fb4e`, `1a3b28e`, `15a6061`, `73c154d`, `ac6debc`, `c12606d`, `a7cbd89`, `5873218`, `b82c270`, `5cb8003`, `45f4e2b`
- Documentation commit: recorded by the commit containing this file

The generated ZIP and synthetic-identity MSIX are unsigned self-contained x64 local-validation artifacts. Neither is an installer or production release.

## Implemented

- Explicit offline-start mode renders a bundled semantic page without contacting a website; native WebView2 UIA exposes its heading and status.
- Quiescent-data backup/restore for browser metadata and private CKB/CCB only. WebView2 profiles/cookies and protected credentials are excluded.
- Backup entries have SHA-256 and length manifests, deterministic ordering/timestamps, size/file-count bounds and atomic final write.
- Restore stages and verifies all entries before replacement, denies traversal/absolute/ADS/reparse paths and archives within the protected root, and restores prior directories if replacement fails.
- The normal app holds an exclusive per-data-root runtime lease. `DCx.exe --recovery` provides a separate keyboard/UIA-accessible backup-and-recovery window; operations fail safely until the main app closes.
- Restore requires a default-unchecked replacement acknowledgment. UI status is an assertive live region, and the main browser exposes an accessible recovery launcher.
- Two self-contained `win-x64` Windows publish trees and fixed-timestamp ZIPs are compared by relative path, length and SHA-256. The payload embeds .NET Core and Windows Desktop 10.0.12 plus the available license/notice files.
- A synthetic per-user, medium-integrity packaged-classic manifest, deterministic generated assets and ordered mapping are packaged by the installed valid Microsoft-signed x64 MakeAppx. Paths/reparse points, child environment, output and time are bounded.
- A strict ZIP/ZIP64 parser canonicalizes only package timestamps. The canonical package must then pass exact MakeAppx unpack comparison. Two independent packages must have identical length and SHA-256, remain `NotSigned`, contain no signature entry and be denied by the real Windows trust verifier.
- WebView2 detection and environment creation are restricted to Stable/Evergreen, and actual version metadata must remain four-part numeric. Missing or unverifiable runtime state never initializes a tab or profile: an assertive keyboard/UIA prerequisite group explains that no download/install occurred, disables browser commands, preserves independent local tools, exposes a read-only official address with explicit copy, and provides a local-only recheck.
- CycloneDX 1.6 inventory and license records cover the seven shipped NuGet dependencies plus the two embedded Microsoft runtime frameworks.
- Repository-source scan has two exact synthetic hostile-input fixtures allowlisted. Published scanning covers all DCx-owned executables/assemblies and text/configuration artifacts. Pinned Microsoft/runtime and NuGet binaries are covered by inventory, notices, official advisory audit and Defender rather than unreliable arbitrary-byte regex matches.
- Current Microsoft Defender state and signature freshness are required. The canonical, valid Microsoft-signed scanner is digest-recorded and runs no-remediation custom scans over the exact publish tree, reproducible ZIP and unsigned MSIX under a minimal OS-derived, secret-free environment.
- The official NuGet advisory source is queried for direct and transitive dependencies through a bounded, schema-checked JSON contract. Parser regression fixtures distinguish an absent zero-result section from real advisory entries, and any vulnerability fails the gate.
- Authenticode state, startup time and working set are recorded without misrepresenting unsigned output.
- Normal-mode unhandled UI, background-task and terminating-process hooks record only after explicit versioned opt-in and never mark an exception handled.
- Each local crash record has exactly six fields: schema version, UTC time, closed component category, closed error category, terminating flag and application version. Messages, stack traces, dumps, prompts, URLs, paths, keystrokes, accounts and user identifiers are structurally absent.
- Reports remain under the per-user application-data root, reject reparse boundaries, use atomic bounded JSON writes and retain at most 20. There is no network transport.
- A keyboard/UIA-accessible privacy dialog exposes the default-unchecked setting, exact data notice, retained count, explicit purge that preserves the current setting and opt-out that purges reports.
- A strict controlled-update envelope canonically binds product identity, four-part MSIX version, issue/expiry times, HTTPS package URI, exact length/SHA-256, package identity and publisher. ECDSA P-256 verification is mandatory before policy or package retrieval.
- Update admission rejects equal/downgrade versions, manifests issued over five minutes in the future or valid longer than 30 days, expired manifests, cross-origin packages, query/user-info/fragment-bearing URIs, non-MSIX paths and noncanonical/unknown JSON.
- The hardened HTTP transport is HTTPS-only and exact-origin, disables redirects, cookies, decompression and proxies, requires reviewed manifest/package media types and enforces 64 KiB manifest and 1 GiB package ceilings while streaming.
- Package staging requires exact byte length/digest plus an injected signature/identity/publisher/version verifier under the same no-write read lease. Reused files are reverified; roots/reparse points are rejected; only the verified package and exact signed manifest are atomically retained. Nothing invokes installation, elevation or package activation.
- A strict MSIX manifest inspector admits one bounded root `AppxManifest.xml` in the Windows 10 foundation namespace and one direct identity. It denies excessive/oversized/case-ambiguous entries, unsupported namespaces, malformed identity/version, DTD and XXE.
- A real Windows read-only signature probe uses the absolute built-in Windows PowerShell 5.1 executable with a constant encoded command, scrubbed environment, package path outside arguments, strict JSON, ten-second deadline and 8 KiB output limits. Timeout and output flood kill the process tree.
- Trust requires Windows status `Valid`, exact signer-subject/manifest-publisher equality and a lowercase signer-certificate SHA-256 that is bound into the approval proposal. The current synthetic package receives a real nonvalid Windows status and is denied; no real signed-positive package is claimed.
- Whisper process telemetry now samples current and peak working set before exit checks and represents an unobservable ultra-short-process sample as unavailable rather than the fabricated value zero. The normal fixture has a bounded observation window; an instant fixture covers the race.
- P13/P14 validators now emit TRX and fail unless exact selected counters prove every test passed. P14 uses its exact test namespace instead of a drifting substring match.
- P13 now rejects legacy Windows PowerShell and requires PowerShell 7.4 or newer on .NET 10 before doing work. Published .NET 10 trust types are loaded through checked reflection so the validator cannot be parsed against an incompatible host or silently bind a changed constructor contract.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p13.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

## Measured result

Recorded evidence directory: `artifacts/test-results/p13-cce4eb1dd19c47f6b7a5d1f589c15a6b/` (generated from exact committed source `45f4e2b`; evidence is intentionally not committed).

- Locked Release build: 0 warnings, 0 errors.
- Reliability tests: passed 23, failed 0, skipped 0; exact counters parsed from the retained TRX.
- Package trust tests: passed 21, failed 0, skipped 0; exact counters parsed from the retained TRX. The native Windows probe denied the complete unsigned local-validation package; positive `Valid` composition used an injected fixture only.
- Current committed-source full suite: passed 502, failed 0, skipped 0; exact counters parsed from retained TRX.
- Publish trees: 419 files, 178,180,928 bytes each; no path/length/SHA-256 differences. They embed .NET Core and Windows Desktop 10.0.12 plus the default-off accessible dependency-transfer workflow, exact-plan-bound product route reviewer, approval/receipt-bound deny-all redirect policy, total admitted-transfer rollback, non-authoritative post-commit progress handling, exact-turn cancellation identity retention, gated synthetic fixture code, authenticated item transfer and protected atomic set coordination/verification, the native Windows hardware/resource selector, hash-pinned P06 catalog/preflight, accessible exact-review/staging, ZIP inspector, runtime stager, protected receipt/approval anchors and staged-tree verifier. They contain no persisted route session, provider redirect rule, completed transfer set/receipt anchor, transfer state, hardware identifier/profile, model/runtime payload, approval state or per-user credential. The protected recipient workflow, benchmark review, global storage confinement and default-unavailable CARE operations window are present. GlobalService simulation APIs/monitor are not in the desktop publish. External FFmpeg, per-user model/benchmark/recipient state, protected credentials and recipient metadata are not bundled.
- Reproducible staging ZIP SHA-256: `a25feb7b371c64b8137bd6133cbd73ba50b8a7fc2bbd38530170a73b68e81569` (74,374,286 bytes).
- Reproducible unsigned MSIX: 73,230,744 bytes; SHA-256 `3b8d8bbc5becd87cae6d7acf58cbe831471f63586e3375dd4c8c087eb1a6da61`; synthetic identity `DCx.AgenticBrowser.LocalValidation`, publisher `CN=DCx Local Validation`, version `1.0.0.0`, x64. MakeAppx semantic pack and exact post-canonicalization unpack passed; real Windows trust returned `NotSigned` and denied it. Signing/install attempts and production actions: 0.
- Trusted MakeAppx version `10.0.19041.5609 (WinBuild.160101.0800)`; executable SHA-256 `f6901ebd5818a623b08a2306499f09334c7dab14bf9c9381814506aa80475f78`; signer-certificate SHA-256 `da209e0fe8bf6363318b5a41e5b65f3391d17bcb8b99b91c320ad2d22ef3469f`.
- CycloneDX components: 9; third-party license records: 9. Three embedded-runtime notice files were copied and hashed.
- Unexpected secret matches: 0 across repository source and 12 selected DCx-owned/published-text files; exact synthetic scanner-fixture matches: 2.
- Microsoft Defender antivirus and real-time protection: enabled; signatures current at version `1.461.79.0`, updated `2026-10-17T18:27:58Z`; publish-tree, ZIP and unsigned-MSIX no-remediation scans returned clean. Trusted scanner SHA-256: `a4291c5b4325895d13d32e232bf50c1b41356306803ac5dc75bba9aecb021141`; signer-certificate SHA-256: `06d10a20570d92f56d784ecf2e50b1277b855e55d19841633b107c27dd5ca778`.
- Official NuGet direct/transitive vulnerability audit: 0 advisories; sole source `https://api.nuget.org/v3/index.json`.
- Authenticode: `NotSigned`.
- Offline UIA heading: `DCx is ready offline`; startup 2,397 ms; working set 464.1 MiB under the provisional 1 GiB ceiling.
- Installed Stable/Evergreen normal browsing passed the P01 native UIA gate. The separately environment-gated missing-runtime fixture was denied when launched without its gate; with the gate, UIA exposed `WebView2 Runtime required`, moved keyboard focus to the heading, disabled browser commands, kept the file/media viewer enabled, exposed only the reviewed official address/copy action and local recheck, created no WebView2 profile, and attempted 0 downloads, 0 installs and 0 production actions.
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
- Bounded manifest/signature trust and denial coverage: `src/DCx.Core/Deployment/MsixPackageTrustVerifier.cs`, `tests/DCx.Core.Tests/Deployment/MsixPackageTrustVerifierTests.cs`.
- Trusted no-remediation malware and official dependency-advisory release gates: `scripts/validate-p13.ps1`, `docs/architecture/adr/0031-no-remediation-release-scanning.md`.
- Reproducible self-contained package builder and synthetic manifest: `scripts/build-unsigned-msix.ps1`, `packaging/msix/AppxManifest.xml`, `packaging/msix/README.md`, `docs/architecture/adr/0032-reproducible-unsigned-self-contained-msix.md`.
- Truthful fast-process Whisper resource telemetry: `src/DCx.Core/LocalModels/WhisperCliTranscriber.cs`, `tests/DCx.Core.Tests/LocalModels/WhisperCliTranscriberTests.cs`, `tests/DCx.FakeWhisperCli/Program.cs`.
- Stable-only WebView2 probe, accessible prerequisite UI and native UIA gate: `src/DCx.Desktop/WebView2RuntimeAvailability.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`, `scripts/validate-p13-webview2.ps1`.
- Supported PowerShell host guard and version-independent .NET trust-type loading: `scripts/validate-p13.ps1`.
- Native product hardware profile/resource-policy code and accessible surface: `src/DCx.Core/LocalModels/WindowsLocalHardwareProfile.cs`, `src/DCx.Core/LocalModels/LocalModelSelector.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`, their tests and `docs/architecture/adr/0062-native-windows-hardware-profile.md`.
- Inert authenticated rollback-capable local-dependency transfer code, tests and accessible policy text: `src/DCx.Core/LocalModels/LocalModelDependencyHttpTransfer.cs`, its plan/catalog validation support, `tests/DCx.Core.Tests/LocalModels/LocalModelDependencyHttpTransferTests.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`, and `docs/architecture/adr/0063-authenticated-rollback-local-dependency-transfer.md`.
- Protected atomic transfer-set coordination, verification and hostile filesystem tests: `src/DCx.Core/LocalModels/LocalModelDependencyTransferSetCoordinator.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelDependencyTransferSetCoordinatorTests.cs`, and `docs/architecture/adr/0064-protected-atomic-local-dependency-transfer-sets.md`.
- Accessible default-off dependency-transfer workflow and gated native UIA fixture: `src/DCx.Desktop/App.xaml.cs`, `src/DCx.Desktop/LocalModelsWindow.xaml`, `src/DCx.Desktop/LocalModelsWindow.xaml.cs`, `scripts/validate-p06-transfer-ui.ps1`, and `docs/architecture/adr/0065-accessible-explicit-local-dependency-transfer.md`.
- Canonical redirect-policy review bound through approval, item/set evidence, protected receipt and accessible preflight: `src/DCx.Core/LocalModels/LocalModelDependencyHttpTransfer.cs`, `src/DCx.Core/LocalModels/LocalModelDependencyTransferSetCoordinator.cs`, the native local-model window and `docs/architecture/adr/0067-bind-dependency-redirect-policy-to-approval.md`.
- Deny-all bounded dependency redirect mechanism and hostile contracts: `src/DCx.Core/LocalModels/LocalModelDependencyHttpTransfer.cs`, `tests/DCx.Core.Tests/LocalModels/LocalModelDependencyHttpTransferTests.cs`, P06 validators and `docs/architecture/adr/0066-deny-by-default-bounded-local-dependency-redirects.md`.
- Total admitted-transfer rollback and rejected-destination preservation: `src/DCx.Core/LocalModels/LocalModelDependencyHttpTransfer.cs`, hostile tests and mandatory P06 contracts, plus `docs/architecture/adr/0068-total-local-dependency-transfer-rollback.md`.
- Non-authoritative post-commit transfer progress and exact-turn cancellation identity retention: their coordinator/client fixtures and `docs/architecture/adr/0069-nonauthoritative-progress-after-transfer-commit.md` plus `docs/architecture/adr/0070-retain-codex-turn-identity-through-cancellation.md`.
- Non-persisted session- and plan-bound exact provider-route review: `src/DCx.Core/LocalModels/LocalModelDependencyRedirectReviewer.cs`, schema-v2 exact routes and pre-request plan enforcement in `src/DCx.Core/LocalModels/LocalModelDependencyHttpTransfer.cs`, pre-storage plan enforcement in `LocalModelDependencyTransferSetCoordinator.cs`, the accessible local-model window, its hostile/composition tests and `docs/architecture/adr/0072-session-bound-exact-provider-route-review.md`.

## Blockers and limits

- The installed x64 MakeAppx and SignTool pass Microsoft publisher validation, but SignTool was not invoked. No production package identity, code-signing certificate/custody or signing approval is available. MSIXMgr and WiX were not required for the local-validation package.
- The self-contained payload removes the external .NET Desktop Runtime dependency but still requires WebView2 Evergreen. Missing/unverifiable runtime detection and accessible recheck now fail closed, but there is no approved download/bootstrap/install, update activation, uninstall or rollback behavior.
- Controlled-update admission is an unwired core boundary. A real read-only Windows Authenticode denial path now exists, but there is no owner-authorized signed-positive MSIX, production update origin/signing-root custody or rotation, accessible update UI, scheduler, activation, restart coordination, installer integration or rollback. The package must be signature/digest/identity/certificate reverified again immediately before a future install; admission is not installation authority.
- P12's accessible package-review window is likewise inert. Its synthetic trust adapter and non-executable approval receipt do not close any installer, signing, activation or clean-VM requirement.
- Microsoft Defender and the official NuGet advisory audit now pass for the exact publish, ZIP, unsigned MSIX and managed dependency closure. They do not provide independent penetration testing, SAST/DAST, native-binary CVE analysis, a second malware engine or signed-installer assessment.
- No clean Windows 11 VM installation/uninstallation/upgrade/rollback run occurred.
- Live SQLite/WAL snapshotting is not claimed; the exclusive lease deliberately requires main DCx to be closed during backup and restore.
- No human JAWS, NVDA or Narrator signoff occurred.
- Crash diagnostics deliberately sacrifice message/stack detail for privacy. They have no export, upload, symbolication or support-ingestion path; any future transfer requires a new reviewed notice, item preview and explicit approval boundary.
- Recovery/readiness utility processes do not initialize this store because they intentionally run outside the normal app's exclusive data lease. Their failures are not represented as covered by the current hooks.
- P13 remains BLOCKED until an authorized production identity/certificate, approved WebView2 bootstrap/install path, signed MSIX install/update/uninstall/rollback on a clean VM, the remaining independent security assessment and human AT acceptance all pass.
