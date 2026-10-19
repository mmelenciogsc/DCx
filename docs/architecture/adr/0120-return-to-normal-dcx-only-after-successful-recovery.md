# ADR 0120: return to normal DCx only after successful recovery

- Status: Accepted for local post-operation handoff; production lifecycle and human AT acceptance remain blocked
- Date: 2026-10-20

## Context

Recovery runs in a separate `--recovery` process so normal DCx cannot hold the application-data lease during backup or restore. After a successful operation, keyboard and screen-reader users had no bounded way to return to normal DCx. A return action must not become an arbitrary process launcher, imply that a failed operation succeeded, or create package-management authority.

## Decision

1. Recovery exposes **Return to DCx** with mnemonic `Alt+T`. It starts disabled and becomes enabled only after backup or restore completes and verifies successfully. Successful operations move keyboard focus to it.
2. A closed result policy admits exactly one process start of the current executable with no arguments, no shell execution or elevation, an expected Recovery-window close, and zero PackageManager, installer, or production actions.
3. The Windows adapter accepts no caller path, arguments, verb, environment, or package identity. It reuses the current-executable policy, sets `UseShellExecute=false` and an empty verb, and supplies no argument.
4. The action is one-use. A failed start keeps Recovery open, restores the action, focuses it, and reports stable content-free text. A successful start closes only Recovery.
5. Native validation must prove initial denial, enablement and focus after both successful backup and restore, the exact direct child and no-argument command line, Recovery closure, normal DCx's offline-ready status, returned keyboard focus, and all authority counters at zero.

## Consequences

Users can complete a closed-app backup or restore and return directly to the ordinary application without navigating through Windows. The boundary does not close another process, execute an installer, activate or remove a package, claim a production backup policy, or establish human JAWS/NVDA/Narrator acceptance.

## Validation

Implementation `2b8df20` passes the exact two-test handoff policy subset and the full 699/699 Windows suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-5280f9f55a7c4f3bb7567bdf992a244c/`: 58/58 reliability, 22/22 package trust, and 3/3 WebView2 policy tests. Native UIA proves initial denial, post-backup and post-restore enablement/focus, UIA access key `T` (the WPF gesture is `Alt+T`), one exact no-argument current-executable child, Recovery closure, normal-window focus, and zero shell/elevation/PackageManager/installer/production effects. Exact P14 evidence is `artifacts/test-results/p14-dbcccc480c9b421781ea65224f4a9acc/`; it remains `NO_GO` with seven blockers and production activation false.

## Verified upstream contracts

- .NET `ProcessStartInfo.ArgumentList`: <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0>
- .NET `ProcessStartInfo.UseShellExecute`: <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0>
- WPF access keys: <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/how-to-use-a-mnemonic-with-a-label>
