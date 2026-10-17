# Release staging and deployment

DCx has no production release. The current validated outputs are an unsigned self-contained x64 staging ZIP and an unsigned synthetic-identity MSIX for local validation. Neither is a distributable installer or release.

## Reproduce local staging evidence

From native PowerShell 7 on Windows:

```powershell
Set-Location D:\DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

The validator performs a locked Release build, reliability tests, two self-contained `win-x64` publish runs, path/length/SHA-256 comparison, two fixed-timestamp ZIP builds, two real MakeAppx package/unpack validations, strict ZIP64 timestamp canonicalization, CycloneDX and license inventory, scoped secret scans, current-signature Microsoft Defender no-remediation scans of the exact publish, ZIP and MSIX, an official NuGet direct/transitive advisory audit, real Windows trust denial, and a real offline WebView2/UIA startup. Generated evidence stays under ignored `artifacts/test-results/p13-*` directories.

The payload embeds .NET Core and Windows Desktop 10.0.12. It does not bundle WebView2 and therefore requires a compatible Evergreen WebView2 Runtime, which is already present on the tested Windows 11 host. DCx restricts detection and environment creation to the Stable channel, rejects channel-suffixed/noncanonical version metadata, and presents a keyboard/UIA-accessible fail-closed prerequisite region when the Runtime is missing or unverifiable. It copies only the reviewed official address on request and never downloads or installs. The MSIX has only a synthetic local-validation identity, no signature, no WebView2 bootstrapper, no wired updater/activation and no tested install/uninstall behavior. Real Windows trust inspection returns `NotSigned` and denies it.

## Controlled update admission boundary

The core now admits an inert update only after verifying a canonical ECDSA P-256 manifest, a newer four-part MSIX version, a short validity window, an exact-origin HTTPS package, byte length/SHA-256 and injected package signature/identity/publisher/version evidence. The HTTP adapter refuses redirects, cookies, proxies, automatic decompression, unreviewed media types and oversized responses. Staged bytes are never executed or installed.

Tests use an ephemeral manifest key, synthetic update bytes, an injected package-trust verifier and a non-network HTTP handler. A separate bounded Windows Authenticode adapter supplies real negative evidence for both the original incomplete fixture and the complete unsigned MakeAppx local-validation package, but no authorized signed-positive package has passed it and it is not wired to installation. There is no production feed, protected signing root/rotation, user-facing update control, activation or rollback. A future installer must reverify the final package immediately before activation; this admission record is not enduring install authority.

## Backup and restore constraint

The tested recovery service includes browser history/bookmarks and private CKB/CCB. It deliberately excludes WebView2 profiles, cookies, browser sessions, global replicas and Windows Credential Manager records, including the P09 trust-policy revision anchor. Archives are deterministic and integrity checked, but current backup/restore is for quiescent application data only. DCx must be closed; live SQLite/WAL snapshotting is not implemented. Production recovery must define how a fresh replica is securely synchronized after OS/credential loss without weakening the protected revision floor.

## Required path to a distributable build

1. Obtain explicit approval for a production package identity, code-signing operation and any separately selected security-assessment tools. The installed SignTool must not be used before that approval.
2. Establish protected code-signing certificate custody and a reviewed version/update policy.
3. Replace the synthetic identity, implement and approve WebView2 installation/bootstrap behavior around the existing accessible detection/recheck UI, and produce a genuinely signed per-user x64 MSIX.
4. Run signed install, offline/online startup, upgrade, rollback and uninstall on clean Windows 11 VMs.
5. Re-run the existing Defender/NuGet gates and complete independent vulnerability/native-binary and AV/SmartScreen assessment against the exact signed artifacts.
6. Complete JAWS, NVDA and Narrator acceptance with recorded human results.

Do not publish either staging artifact or describe either as installable. Deployment to the Ubuntu server, external publication, signing, installation and production activation require separate owner approval.
