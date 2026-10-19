# ADR 0119: open exact recovery mode without creating backup or uninstall authority

- Status: Accepted for local user-mode handoff; backup completion and production uninstall remain blocked
- Date: 2026-10-20

## Context

ADR 0118 asks the user to make a separate backup choice before a future uninstall, but its review offered no direct route to DCx's existing closed-app recovery mode. The pre-existing main-window launcher used shell execution, duplicated process-start logic, and surfaced raw exception text. The uninstall workflow needs a keyboard/screen-reader-accessible handoff without implying that opening recovery created a backup, closed DCx, or authorized removal.

## Decision

1. Define one closed result shape for a recovery handoff: exactly one process start; the current DCx executable; the exact `--recovery` argument; no shell execution, elevation, backup creation, application-close request, installer process, or production action.
2. The Windows adapter obtains only `Environment.ProcessPath`, requires a fully qualified existing `.exe`, uses `ProcessStartInfo.ArgumentList`, sets `UseShellExecute=false` and an empty verb, then starts that same executable. It accepts no caller path, command, argument, verb, or environment mutation.
3. Both the main Recovery button and uninstall review use this adapter and map every failure to stable content-free UI text. The uninstall review exposes `Alt+R`, explains that it starts a separate local window only, and leaves actual uninstall controls unavailable.
4. Recovery retains ADR 0010's exclusive data lease. Opening the window creates no backup; the user must close main DCx and separately choose Create backup. The handoff neither checks nor asserts backup completion and cannot satisfy the backup-choice acknowledgment by itself.
5. Native validation must observe one new direct child whose executable equals the running DCx executable and whose complete command line contains only `--recovery`; verify the Recovery window and its defined initial path focus; and prove that main DCx remains open while backup, close, installer, elevation, PackageManager, uninstall, and production counters remain zero.

## Consequences

Keyboard and screen-reader users can move directly from uninstall review to the existing recovery workflow without copying a command or navigating elsewhere. Shell association and arbitrary-command risks are removed from the main recovery launcher. This does not create a backup, close the app, verify a backup, coordinate uninstall, or provide post-removal result handoff. Production backup/retention policy, signed lifecycle execution, rollback, clean-VM acceptance, and human JAWS/NVDA/Narrator testing remain required.

## Validation

Implementation `211c204` passes 15/15 focused recovery/removal tests and the full 698/698 Windows suite with zero build warnings/errors. Exact P13 evidence is `artifacts/test-results/p13-35caa0ae56644312949663ba808eeb52/`: 57/57 reliability, 22/22 package trust and 3/3 WebView2 policy. Native UIA exposes five distinct uninstall access keys, observes one exact recovery child and initial backup-path focus, and records backup created false plus zero close/installer/elevation/production actions. PackageManager and uninstall attempts remain zero. Exact P14 evidence is `artifacts/test-results/p14-51142f64d9e74c6e88d04d50d648d47a/`; it remains `NO_GO` with seven blockers and activation false.

## Verified upstream contracts

- .NET `ProcessStartInfo.ArgumentList`: <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0>
- .NET `ProcessStartInfo.UseShellExecute`: <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0>
- .NET `Process.Start(ProcessStartInfo)`: <https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.start?view=net-10.0>
