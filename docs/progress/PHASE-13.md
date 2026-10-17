# P13 — Packaging, security and reliability

- Status: BLOCKED (tooling/signing/clean-VM/human-accessibility prerequisites absent)
- Commands/results: none
- Changed files: none
- Commit: none

Required gate includes clean Windows 11 VM install/uninstall/update, SBOM/licenses, signing pathway, offline startup, rollback and human assistive-technology signoff.

Active work: audit available Windows packaging/signing/security tooling, generate dependency/license/SBOM evidence, and select a user-mode installer path. No unsigned artifact will be represented as a production release; clean-VM and human assistive-technology gates remain mandatory.

## Measured audit

- Windows PowerShell 5.1.26100.9444 and .NET SDK 10.0.401 are available.
- `winget.exe` is present as a per-user Windows app alias.
- `makeappx.exe`, `signtool.exe`, `msixmgr.exe`, WiX (`wix`, `heat`, `candle`, `light`) and SBOM/vulnerability tools (`syft`, `trivy`, `osv-scanner`, `grype`) are absent from the current command paths.
- No code-signing identity, clean Windows 11 VM, installer test VM or human JAWS/NVDA/Narrator signoff is available.

Installing packaging/security tools, acquiring a signing certificate, or changing the machine is not authorized. P13 remains blocked; no installer or release artifact is claimed.
