# Release staging and deployment

DCx has no production release. The current validated output is an unsigned, framework-dependent Windows staging bundle—not an installer.

## Reproduce local staging evidence

From native Windows PowerShell:

```powershell
Set-Location D:\DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

The validator performs a locked Release build, reliability tests, two publish runs, path/length/SHA-256 comparison, two fixed-timestamp ZIP builds, CycloneDX and license inventory, repository and published-binary secret scans, current-signature Microsoft Defender no-remediation scans of the exact publish and ZIP, an official NuGet direct/transitive advisory audit, Authenticode inspection, and a real offline WebView2/UIA startup. Generated evidence stays under ignored `artifacts/test-results/p13-*` directories.

The output currently requires compatible .NET Desktop and Microsoft Edge WebView2 runtimes already installed. It has no bootstrapper, package identity, wired updater/activation, uninstall registration or signature.

## Controlled update admission boundary

The core now admits an inert update only after verifying a canonical ECDSA P-256 manifest, a newer four-part MSIX version, a short validity window, an exact-origin HTTPS package, byte length/SHA-256 and injected package signature/identity/publisher/version evidence. The HTTP adapter refuses redirects, cookies, proxies, automatic decompression, unreviewed media types and oversized responses. Staged bytes are never executed or installed.

Tests use an ephemeral manifest key, synthetic package bytes, an injected package-trust verifier and a non-network HTTP handler. A separate bounded Windows Authenticode adapter supplies real negative evidence for the unsigned/incomplete fixture, but no authorized signed-positive package has passed it and it is not wired to installation. There is no production feed, protected signing root/rotation, user-facing update control, activation or rollback. A future installer must reverify the final package immediately before activation; this admission record is not enduring install authority.

## Backup and restore constraint

The tested recovery service includes browser history/bookmarks and private CKB/CCB. It deliberately excludes WebView2 profiles, cookies, browser sessions, global replicas and Windows Credential Manager records, including the P09 trust-policy revision anchor. Archives are deterministic and integrity checked, but current backup/restore is for quiescent application data only. DCx must be closed; live SQLite/WAL snapshotting is not implemented. Production recovery must define how a fresh replica is securely synchronized after OS/credential loss without weakening the protected revision floor.

## Required path to a distributable build

1. Obtain explicit approval to install supported Windows MSIX/signing tooling and any separately selected security-assessment tools.
2. Establish protected code-signing certificate custody and a reviewed version/update policy.
3. Produce a per-user x64 MSIX with .NET Desktop and WebView2 prerequisite handling.
4. Run signed install, offline/online startup, upgrade, rollback and uninstall on clean Windows 11 VMs.
5. Re-run the existing Defender/NuGet gates and complete independent vulnerability/native-binary and AV/SmartScreen assessment against the exact signed artifacts.
6. Complete JAWS, NVDA and Narrator acceptance with recorded human results.

Do not publish the staging ZIP or describe it as installable. Deployment to the Ubuntu server, external publication and production activation require separate owner approval.
