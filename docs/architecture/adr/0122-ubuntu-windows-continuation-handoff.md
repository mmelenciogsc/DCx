# ADR 0122: Separate Ubuntu continuation development from Windows product validation

- Status: Accepted for continuation planning and U00/U16 handoff.
- Date: 2026-10-22
- Source: additive `DCx_UI_Continuation_Kit`.

## Decision

Use an isolated Ubuntu feature branch for documentation, portable policy/service tests, review, and later approved Debian/Docker simulation work. Keep Windows as the native WPF/WebView2/installer/host-bridge and interactive validation target. Exchange only reviewed source or checksum-bound artifacts. The Ubuntu server must not become a remote executor for customer Codex sessions or a store for private customer prompts, files, credentials, or private CKB/CCB indexes.

Every Windows command is SHA-bound and executed through the configured restricted SSH target. A command-visible SSH result counts only as automation evidence. Foreground desktop, WebView2 accessibility tree, user login/MFA, consent, camera/microphone, Bluetooth, installer, and JAWS/NVDA/Narrator results require a live Windows session and, where applicable, human validation.

## Rationale and limits

This preserves the current native product boundary while enabling reproducible Ubuntu-side review and future Debian/Docker service validation. It prevents a Linux build, remote browser, or SSH response from being mislabeled as a tested accessible Windows interaction.

The decision does not approve .NET SDK installation, container deployment, Windows driver/service changes, device forwarding, browser login, account use, model acquisition, financial action, publication, merge, or production rollout. U03 must separately prove the shared live WebView2 session and narrow host bridge before broader UI migration.
