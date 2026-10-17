# Release staging and deployment

DCx has no production release. The current validated output is an unsigned, framework-dependent Windows staging bundle—not an installer.

## Reproduce local staging evidence

From native Windows PowerShell:

```powershell
Set-Location D:\DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

The validator performs a locked Release build, reliability tests, two publish runs, path/length/SHA-256 comparison, two fixed-timestamp ZIP builds, CycloneDX and license inventory, repository and published-binary secret scans, Authenticode inspection, and a real offline WebView2/UIA startup. Generated evidence stays under ignored `artifacts/test-results/p13-*` directories.

The output currently requires compatible .NET Desktop and Microsoft Edge WebView2 runtimes already installed. It has no bootstrapper, package identity, updater, uninstall registration or signature.

## Backup and restore constraint

The tested recovery service includes browser history/bookmarks and private CKB/CCB. It deliberately excludes WebView2 profiles, cookies, browser sessions, global replicas and Windows Credential Manager secrets. Archives are deterministic and integrity checked, but current backup/restore is for quiescent application data only. DCx must be closed; live SQLite/WAL snapshotting is not implemented.

## Required path to a distributable build

1. Obtain explicit approval to install supported Windows MSIX/signing/security tooling.
2. Establish protected code-signing certificate custody and a reviewed version/update policy.
3. Produce a per-user x64 MSIX with .NET Desktop and WebView2 prerequisite handling.
4. Run signed install, offline/online startup, upgrade, rollback and uninstall on clean Windows 11 VMs.
5. Run supported dependency/vulnerability and AV/SmartScreen checks against the exact signed artifacts.
6. Complete JAWS, NVDA and Narrator acceptance with recorded human results.

Do not publish the staging ZIP or describe it as installable. Deployment to the Ubuntu server, external publication and production activation require separate owner approval.
