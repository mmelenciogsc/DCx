# P13 — Packaging, security and reliability

- Status: BLOCKED (substantial local reliability/release-staging checks pass; installer/signing/scanner/clean-VM/human-AT gates absent)
- Implementation commit: `82452fe`
- Documentation commit: recorded by the commit containing this file

The generated ZIP is an unsigned framework-dependent staging artifact. It is not an installer or production release.

## Implemented

- Explicit offline-start mode renders a bundled semantic page without contacting a website; native WebView2 UIA exposes its heading and status.
- Quiescent-data backup/restore for browser metadata and private CKB/CCB only. WebView2 profiles/cookies and protected credentials are excluded.
- Backup entries have SHA-256 and length manifests, deterministic ordering/timestamps, size/file-count bounds and atomic final write.
- Restore stages and verifies all entries before replacement, denies traversal/absolute/ADS/reparse paths and archives within the protected root, and restores prior directories if replacement fails.
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

Recorded evidence directory: `artifacts/test-results/p13-6bf609a806374f0a865bb9cc699aadf2/` (generated and intentionally not committed).

- Locked Release build: 0 warnings, 0 errors.
- Reliability tests: passed 12, failed 0, skipped 0; 163 ms.
- Publish trees: 42 files, 62,656,931 bytes each; no path/length/SHA-256 differences.
- Reproducible staging ZIP SHA-256: `032716a2dc1b7f5cb794cf21785e686982b81ecc3f31912fbadda4fca66ffe14`.
- CycloneDX components: 7; third-party license records: 7.
- Unexpected secret matches: 0; exact synthetic scanner-fixture matches: 2.
- Authenticode: `NotSigned`.
- Offline UIA heading: `DCx is ready offline`; startup 2,089 ms; working set 461.2 MiB under the provisional 1 GiB ceiling.
- Production actions: 0.

## Blockers and limits

- `makeappx.exe`, `signtool.exe`, MSIXMgr and WiX are absent. No code-signing identity is available. Installing tooling or acquiring a certificate requires approval.
- The staging ZIP requires the pinned .NET desktop runtime and WebView2 runtime and provides no install, update, uninstall or runtime bootstrap behavior.
- No supported vulnerability scanner is installed; SBOM/license generation is not a vulnerability result.
- No clean Windows 11 VM installation/uninstallation/upgrade/rollback run occurred.
- Backup currently targets quiescent data through a tested core service, not an end-user recovery UI. Live SQLite/WAL snapshotting is not claimed; DCx must be closed before production backup/restore tooling uses it.
- No human JAWS, NVDA or Narrator signoff occurred.
- P13 remains BLOCKED until signed MSIX install/update/uninstall on a clean VM, vulnerability and AV scans, WebView2/.NET prerequisite handling, recovery UI/closed-app orchestration and human AT acceptance all pass.
