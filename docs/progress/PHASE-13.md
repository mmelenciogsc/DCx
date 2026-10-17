# P13 — Packaging, security and reliability

- Status: In progress (audit only)
- Commands/results: none
- Changed files: none
- Commit: none

Required gate includes clean Windows 11 VM install/uninstall/update, SBOM/licenses, signing pathway, offline startup, rollback and human assistive-technology signoff.

Active work: audit available Windows packaging/signing/security tooling, generate dependency/license/SBOM evidence, and select a user-mode installer path. No unsigned artifact will be represented as a production release; clean-VM and human assistive-technology gates remain mandatory.
