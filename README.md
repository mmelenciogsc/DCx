# DCx / Agentic Browser

DCx is an accessibility-first native Windows 11 agentic browser and computer environment. The repository is under active phased implementation; it is not a validated release.

Authoritative requirements and phase gates live in [`DCx_Codex_Handoff_Kit`](DCx_Codex_Handoff_Kit/README.md). Current evidence and blockers live in [`docs/progress/STATUS.md`](docs/progress/STATUS.md).

## Build on Windows

```powershell
dotnet restore DCx.slnx --locked-mode
dotnet build DCx.slnx --configuration Release --no-restore
dotnet test DCx.slnx --configuration Release --no-build
```

Or run `scripts/ci.ps1`. WSL users may invoke the same native Windows gate through `scripts/verify-p00.sh`.

## Current scope

The initial executable shell uses WPF and Microsoft Edge WebView2. The choice is recorded in `docs/architecture/adr/0001-wpf-webview2.md` and may be revisited if measured constraints change.

