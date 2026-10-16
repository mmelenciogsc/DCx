# P00 — Audit and bootstrap

- Status: Complete (local native-Windows gate)
- Started: 2026-10-17
- Gate owner: implementation agent; manual/external claims excluded
- Milestone commit: `a98195a`

## Requirements and assumptions

The four handoff files were read in full before implementation. The pre-existing `DCx` directory contained only the handoff kit and one prompt file; it was not a Git repository and contained no application implementation to preserve. WSL is a development surface only. No Ubuntu-server connection/deployment, privileged installation, paid provider call, publication, or financial transaction was performed.

## Measured audit

| Item | Result |
|---|---|
| Windows | Windows 11 Home Single Language 10.0.26200 x64 |
| Windows identity | `CYBORG\marx`; development session reports administrator membership; app manifest is `asInvoker` |
| WSL | WSL2 kernel 6.18.40.1; Ubuntu 22.04.3; Windows interop present |
| CPU/RAM | Intel i7-12650H, 10 cores/16 logical; 16,870,072,320 bytes host RAM; WSL limit 7.6 GiB |
| GPU | NVIDIA RTX 4060 Laptop GPU; driver 610.78; 8,188 MiB VRAM; compute capability 8.9; Intel UHD also present |
| Disk | D: 932 GB total, 812 GB used, 120 GB free (88% used); WSL root 890 GB free |
| Windows .NET | SDK 10.0.401; runtimes 8.0 and 10.0; Windows Desktop runtime 10.0.12 |
| Windows build tools | VS Build Tools 2019 16.11.50; Windows SDK 10.0.19041; no `devenv`; no installed .NET workloads |
| UI frameworks | WPF templates present; WinUI templates absent |
| Browser runtime | Microsoft Edge WebView2 Runtime 155.0.4283.45 |
| Accessibility | JAWS 2023/2024/2025, NVDA 2026.1.1, Narrator installed |
| WSL tooling | Git 2.34.1, Node 22.23.2, npm 10.9.8, Codex CLI 0.146.0, Docker CLI 29.1.3, CMake 3.22.1 |
| Windows tooling | Git 2.47.1, Node 22.18.0; Windows Codex executable not found |
| Containers | Docker daemon access denied for current WSL user; deferred to P09 and not worked around with elevation |
| SSH | Windows OpenSSH present and one host entry configured; no connection attempted and no host details or keys logged |

## Framework decision

ADR 0001 selects WPF/.NET 10 plus WebView2 because that stack is supported by the installed native Windows SDK and produced a real executable without installing tools. Current WinUI templates/workloads are absent. The decision is reversible if a required measured gate later fails.

## Reproduction commands

Environment examples:

```bash
uname -a
lsb_release -a
free -h
lscpu
nvidia-smi --query-gpu=name,driver_version,memory.total,memory.free,compute_cap --format=csv,noheader
powershell.exe -NoProfile -Command 'dotnet --info; dotnet workload list'
powershell.exe -NoProfile -Command 'dotnet new list wpf; dotnet new list winui'
```

First package resolution, used once to create committed lock files:

```powershell
Set-Location D:\DCx
dotnet restore DCx.slnx
```

Reproducible locked gate:

```bash
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\smoke-desktop.ps1
```

Equivalent Windows steps are in `scripts/ci.ps1`.

## Test history

The first build failed with 37 compile diagnostics caused by two authored omissions: `System.IO` in the WPF code and the xUnit global import in tests. Both were corrected without changing test intent.

Final pre-documentation run:

- Native Release build: passed; 0 warnings, 0 errors; 1.38 seconds.
- Unit tests: passed 14, failed 0, skipped 0; 525 ms.
- Desktop smoke: `DCx.exe` launched as PID 16864 with title `DCx / Agentic Browser`; UIA main window found; address, back, navigate, status and interactive-page elements exposed; test process closed.
- Generated executable: `src/DCx.Desktop/bin/Release/net10.0-windows10.0.19041.0/DCx.exe`, 162,304 bytes in that run.

Final locked CI-equivalent run on Windows 11 build 26200.9457:

- Restore: all three projects restored from committed lock files; 64–144 ms per project.
- Release build: passed; 0 warnings, 0 errors; 0.73 seconds.
- Unit tests: passed 14, failed 0, skipped 0; 28 ms.
- Scripted desktop smoke: PID 9320; title `DCx / Agentic Browser`; required UIA elements 5/5; process closed cleanly.
- Repository secret-pattern scan: no matches.
- `git diff --check`: passed.

The GitHub Actions workflow was not remotely executed because no remote repository exists. Its native Windows commands are the same commands that passed through `scripts/verify-p00.sh`; this limitation is not represented as a remote-CI pass.

## Changed files

- Repository controls: `.editorconfig`, `.gitattributes`, `.gitignore`, `global.json`, `NuGet.Config`, central build/package files, solution, CI workflow.
- Native code: `src/DCx.Core`, `src/DCx.Desktop`.
- Tests and scripts: `tests/DCx.Core.Tests`, `scripts/ci.ps1`, `scripts/verify-p00.sh`, `scripts/smoke-desktop.ps1`.
- Governance/docs: `AGENTS.md`, root README/license/notice, architecture ADRs, security/privacy/accessibility/deployment/reference/progress docs, `RESUME.md`.
- Authoritative handoff files were retained unchanged.

## Security and accessibility status

- Initial address policy permits only HTTP(S) and rejects file, JavaScript, data and FTP schemes from the address bar.
- WebView2 data uses a DCx-owned profile path; no Chrome data is imported.
- App requests no elevation.
- UIA smoke confirms named/focusable controls only. JAWS/NVDA/Narrator behavior remains untested and must not be inferred.
- Secrets were neither requested nor logged.

## Blockers and limitations

- No hosted Git remote exists, so the checked-in GitHub workflow cannot be claimed as remotely executed. The same commands are executed locally on native Windows.
- Docker daemon permission is unavailable to this WSL user; relevant only from P09.
- WinUI 3 was not built because its template/workload is absent; installing it would be unnecessary privileged setup for the selected WPF path.
- Disk is already 88% used; P06 model selection must budget space before any download.
