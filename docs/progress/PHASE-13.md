# P13 — Packaging, security and reliability

- Status: BLOCKED (substantial local reliability/release-staging checks pass; installer/signing/scanner/clean-VM/human-AT gates absent)
- Implementation commits: `82452fe`, `3e9b172`
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

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p13.ps1
```

## Measured result

Recorded evidence directory: `artifacts/test-results/p13-19dc6457cd1c4e6e8e524a5d6bdcffa2/` (generated and intentionally not committed).

- Locked Release build: 0 warnings, 0 errors.
- Reliability tests: passed 14, failed 0, skipped 0; 187 ms.
- Current full suite after P10 sandbox-contract hardening: passed 155, failed 0, skipped 0; about 1 second.
- Publish trees: 42 files, 62,760,355 bytes each; no path/length/SHA-256 differences.
- Reproducible staging ZIP SHA-256: `e02ee45103d999030cadad88f5e34946186402a90bf403450b338a4eb781242e`.
- CycloneDX components: 7; third-party license records: 7.
- Unexpected secret matches: 0; exact synthetic scanner-fixture matches: 2.
- Authenticode: `NotSigned`.
- Offline UIA heading: `DCx is ready offline`; startup 2,253 ms; working set 461.2 MiB under the provisional 1 GiB ceiling.
- Recovery UIA heading: `DCx backup and recovery`; main-app launcher, path input, create, default-unchecked approval, restore and assertive status are exposed.
- Backup while main DCx held the lease was denied and wrote no archive. After main exit, backup and approved restore passed; restore without approval was denied; excluded synthetic WebView2 cookie data remained unchanged.
- Production actions: 0.

## Blockers and limits

- `makeappx.exe`, `signtool.exe`, MSIXMgr and WiX are absent. No code-signing identity is available. Installing tooling or acquiring a certificate requires approval.
- The staging ZIP requires the pinned .NET desktop runtime and WebView2 runtime and provides no install, update, uninstall or runtime bootstrap behavior.
- No supported vulnerability scanner is installed; SBOM/license generation is not a vulnerability result.
- No clean Windows 11 VM installation/uninstallation/upgrade/rollback run occurred.
- Live SQLite/WAL snapshotting is not claimed; the exclusive lease deliberately requires main DCx to be closed during backup and restore.
- No human JAWS, NVDA or Narrator signoff occurred.
- P13 remains BLOCKED until signed MSIX install/update/uninstall on a clean VM, vulnerability and AV scans, WebView2/.NET prerequisite handling and human AT acceptance all pass.
