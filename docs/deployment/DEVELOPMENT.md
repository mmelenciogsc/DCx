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

The recorded development host has one approved vision fixture alias but no installed embedding model or real Whisper backend. Do not add either dependency without owner approval and recorded source, version, license, digest, resource and quality evidence. A malformed selection file fails closed; it must not be replaced with an environment-variable bypass.

## Ubuntu service

No real-server deployment is authorized. P09 must first provide a local Compose simulation and a reviewed deployment manifest. Connecting to, installing on, or changing the owner's Ubuntu server requires explicit approval.
