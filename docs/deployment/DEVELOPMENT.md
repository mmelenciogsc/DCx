# Reproducible development and build

## Required Windows tooling

- Windows 11 x64.
- .NET SDK 10.0.401 (pinned by `global.json`).
- Microsoft Edge WebView2 Runtime.
- Network access to the single configured NuGet source for first restore.

Visual Studio is not required for the current command-line build. WSL is optional and not used at product runtime.

## Native Windows gate

From PowerShell 7.4 or newer on .NET 10:

```powershell
Set-Location D:\DCx
dotnet restore DCx.slnx --locked-mode
dotnet build DCx.slnx --configuration Release --no-restore
dotnet test DCx.slnx --configuration Release --no-build
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-p13.ps1
```

From the current WSL development environment:

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
```

Build outputs are under each project's `bin/Release` directory and are ignored by Git. Do not commit credentials, local browser profiles, package caches, signing keys, or test-result artifacts.

## Already-installed local models

The current P06 gate expects a Windows-native Ollama service at the loopback origin `http://localhost:11434/`. DCx does not download, install or retag models. In the native app, open **Local models**, select a unique name ending in its matching SHA-256 digest, read the complete license text, check the default-off approval, and choose the eligible vision or embedding role. The per-user selection can be cleared independently for each role.

An already-installed Whisper backend may be registered in the same window by selecting the local executable, model, runtime license and model license. Read both complete licenses and all four SHA-256 values, then check the default-off approval and save. Saving revalidates all four files; clearing removes only DCx state, not the selected files. DCx does not download or install Whisper from this surface, and registration alone does not establish publisher authenticity, license suitability or accuracy.

The files/media viewer can directly transcribe an admitted local WAV through that exact binding. MP3/MP4 transcription additionally requires a separately approved FFmpeg executable/license binding and uses a bounded, hash-reverified, leased temporary WAV that is deleted after Whisper finishes. It exposes cancellation and a read-only accessible transcript but does not persist or export transcript text. Frame extraction uses the same approved FFmpeg boundary and never selects an executable from `PATH`.

The local-model window reviews an already-installed FFmpeg executable plus its complete license and real Windows signature status before either media consumer can use it. Approval is per-user, explicit and revocable; no acquisition action exists. Do not bundle or approve the measured unsigned GPLv3 host build for distribution without owner and legal review.

The recorded development host has one approved vision fixture alias but no installed embedding model or real Whisper backend. Do not add either dependency without owner approval and recorded source, version, license, digest, resource and quality evidence. A malformed selection file fails closed; it must not be replaced with an environment-variable bypass. Run P06 with PowerShell 7 because its native evidence loads the .NET production assembly:

```powershell
pwsh.exe -NoProfile -File .\scripts\validate-p06.ps1
```

The **CARE recipients** window is a local-only owner-preparation surface. It requires exact pseudonym, email and digest review; uses separate default-unchecked approvals for save and revoke; stores the exact binding only in current-user Windows Credential Manager; and writes only minimized non-email metadata to its index. It never establishes PayPal verification or eligibility and exposes no payment, payout or network action. The P11 native validator uses a generated fixture address and deletes both the snapshot and protected credential in a `finally` cleanup path.

The global-service assembly contains schema-v11 failed-item remediation, verified-event dependency quarantine, minimized operator-decision/disposition audit, durable aggregate alert outbox and opaque blocked-case boundaries for local handler-backed tests. Quarantine admits only an officially verified sale/correction with a missing durable binding. `DurableCareOperationsSource` defaults processing disabled and binds exact reviewed aggregate value, approval and the bounded batch to one same-database transaction. A process-boundary fixture exists only when `DCX_GLOBAL_SIMULATION=1`, `DCX_GLOBAL_CARE_OPERATIONS_API=1`, `DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256` and the separate non-authorizing `DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256` are configured; replay mutation also requires `DCX_GLOBAL_CARE_PROCESSING=1`. It exposes aggregate health/process plus opaque blocked-case list/disposition routes under `/api/care/operations`, uses the simulation origin and rate limiter, and is not production deployment guidance. Blocked-case dispositions accept only explicitly approved `investigation_requested`/`retained_blocked`, leave events quarantined and make zero financial/provider calls. The pseudonym digest is sensitive/linkable and must never be derived from the bearer token. An optional read-only monitor requires `DCX_GLOBAL_CARE_MONITOR=1`; `DCX_GLOBAL_CARE_MONITOR_INTERVAL_MS` is bounded from 100 through 300000 and defaults to 30000. It emits only fixed aggregate log transitions and never processes work. Its same-database outbox and at-least-once delivery receipt survive restart; no external notification, human acknowledgment/escalation, approved closure/requeue policy, retention/pruning or distributed-service scheduler exists. The Windows CARE operations window still defaults unavailable; `DCX_CARE_OPERATIONS_FIXTURE=1` enables only fixed generated UIA data. Do not wire the loopback client into normal desktop launches or add credentials/provider access until real identity, MFA/TLS, secret custody, centralized externally anchored audit, distributed fencing, closure/requeue and external notification/escalation policy, retention, production monitoring and credentialed sandbox acceptance are approved.

## Ubuntu service

No real-server deployment is authorized. The bounded command below uses the preconfigured `ubuntu-server` SSH alias only for fixed read-only, nonprivileged capability checks; it must not be expanded into file transfer, build, container or service actions without approval.

```powershell
pwsh.exe -NoProfile -File .\scripts\audit-ubuntu-server.ps1
```

The recorded audit found Ubuntu 24.04, .NET/ASP.NET 10, Docker/Compose and Bubblewrap, but denied Docker daemon access, found no writable cgroup delegation and failed the exact Bubblewrap network-isolation smoke. P09 still needs explicit authority for any access change or Compose deployment. P10 still needs an approved remediation that preserves network denial and measured resource isolation.
