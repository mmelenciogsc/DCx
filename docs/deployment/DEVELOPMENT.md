# Reproducible development and build

## Required Windows tooling

- Windows 11 x64.
- .NET SDK 10.0.401 (pinned by `global.json`).
- Microsoft Edge WebView2 Runtime.
- Network access to the single configured NuGet source for first restore.

Visual Studio is not required for the current command-line build. WSL is optional and not used at product runtime.

## Native Windows gate

From PowerShell:

```powershell
Set-Location D:\DCx
dotnet restore DCx.slnx --locked-mode
dotnet build DCx.slnx --configuration Release --no-restore
dotnet test DCx.slnx --configuration Release --no-build
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

## Ubuntu service

No real-server deployment is authorized. P09 must first provide a local Compose simulation and a reviewed deployment manifest. Connecting to, installing on, or changing the owner's Ubuntu server requires explicit approval.
