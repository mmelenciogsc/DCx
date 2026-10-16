# P03 — External native apps and terminal broker

- Status: Complete (automated native-Windows gate; human assistive-technology rows unverified)
- Implementation commit: `74b7be1`

Required gate: restricted PowerShell/process/UIA contracts, manual takeover, and destructive-operation denial tests.

## Initial plan and assumptions

Define broker contracts and policy before launching any native process. Start with allowlisted unelevated Calculator/Notepad and a repository-owned demo app, explicit executable/argument arrays, working-directory scopes, output/time limits and deny rules for shell interpolation, elevation, deletion and unapproved paths. Add a focus lease and user takeover UI analogous to P02. Use native UI Automation patterns; pixel input is not part of the validated path. No install/elevation or destructive command will be attempted.

## Implemented

- Typed process requests for only controlled fixture launch, Notepad, Calculator and read-only path inspection. Unknown operation/argument combinations deny by default.
- Canonical workspace containment with parent traversal and existing reparse-point/junction denial.
- `ProcessStartInfo.ArgumentList` throughout; no interpolated command line, shell execution or elevation.
- Repository-owned PowerShell script with a single `ValidateSet` operation, literal paths, no profiles, a 10-second limit, bounded output and exact-child-tree cancellation.
- UI Automation limited to broker-tracked process IDs, bounded automation identifiers/text, and Value/Invoke/Focus patterns. Pixel input is absent.
- Accessible native-app control window with separate scoped UIA approval, Take control, explicit Resume agent, focus restoration, Close controlled app and a bounded privacy-safe action log.
- Separate repository-owned WPF fixture process for deterministic external-app handoff evidence.
- Policy unit coverage for allowed operations, unexpected arguments, path traversal, missing targets and a symlink escape to an existing outside target.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\smoke-desktop.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p02.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p03.ps1
```

## Measured final results

Combined native Windows 11 regression on build 26200.9457:

- Locked restore and Release build: passed; 0 warnings, 0 errors; 3.24 seconds.
- Core tests: passed 35, failed 0, skipped 0; 591 ms.
- P00 UIA smoke: 5/5 required elements; title `Example Domain - DCx / Agentic Browser`.
- P01 browser regression: passed.
- P02 shared-control regression: passed.
- P03 UIA E2E: passed.
  - Restricted PowerShell inspection completed without displaying or logging file content.
  - Controlled application ran as a process distinct from DCx.
  - UI Automation required a separate named approval group.
  - Agent prepared the fixed field, then yielded with keyboard focus in the external app.
  - User-entered fixture text survived explicit resume; focus was restored.
  - Five accessible log entries omitted the user's field value.
  - The controlled process closed only after explicit activation.
- `git diff --check`: passed before implementation commit.
- Repository secret-pattern scan: no matching files.

## Security and accessibility observations

- The first E2E version searched only top-level UIA children for DCx's owned control window. Native evidence showed owned WPF windows are nested in this automation tree; the validator now uses a process-and-automation-ID descendant lookup.
- The reparse-point test uses a real Windows directory symbolic link into an outside temporary directory. The request is denied before PowerShell execution.
- The action log records process IDs, state transitions, exit status and user-value length, never the user value or inspected file content.
- The app and fixture remain unelevated. Cancellation targets only the exact broker-created PowerShell child tree.
- Automated UIA is not evidence of JAWS, NVDA or Narrator usability. The P03 human checklist remains wholly UNVERIFIED.

## Changed files

- Broker contracts/policy: `src/DCx.Core/Broker`.
- Native process/UIA adapters: `src/DCx.Desktop/Broker`.
- Accessible control surface and shell entry: `src/DCx.Desktop/NativeAppControlWindow.xaml*`, `src/DCx.Desktop/MainWindow.xaml*`.
- Fixed read-only command: `scripts/broker/read-only.ps1`.
- Policy and native E2E tests: `tests/DCx.Core.Tests/Broker`, `tests/DCx.NativeFixture`, `scripts/validate-p03.ps1`.
- Solution registration: `DCx.slnx`.
- Human checklist: `docs/accessibility/P03_NATIVE_APP_CHECKLIST.md`.

## Known limitations carried forward

- The real E2E target is the repository-owned fixture. Notepad and Calculator are allowlisted but not claimed as validated UIA targets.
- The broker does not provide arbitrary terminal commands, arbitrary executable launch, elevation, deletion or installation.
- Broker script signing and installed-package integrity are P13 concerns; the development script is repository-owned but not Authenticode-signed.
- Human JAWS/NVDA/Narrator operation and physical interruption remain release blockers.
