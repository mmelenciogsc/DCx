# DCx architecture overview

## Runtime topology

DCx is split into three independently secured planes.

1. **Native Windows client** — WPF shell, WebView2 profiles, file/document/media views, accessibility layer, task UI, local orchestrator, local model adapters, private knowledge stores, read-only global replica, and narrowly permissioned Windows brokers.
2. **Provider plane** — OpenAI APIs or an eligible, approved Codex/ChatGPT sign-in path. Provider access is optional, explicitly configured, cancellable, and never receives local private data without task-scoped consent.
3. **Shared Ubuntu service** — curated global catalog, signed releases, contribution quarantine/validation, entitlements, pseudonymous qualified-usage receipts, and CARE sandbox/production accounting. It does not run customer Codex sessions or routinely receive customer prompts/files.

The owner's Ubuntu server is not a development scratch host. Deployment requires an explicit approval gate after local simulation.

## Windows client components

- **Shell:** WPF/.NET 10 executable, keyboard-first controls, accessible status and approvals.
- **Browser surface:** Microsoft Edge WebView2 with DCx-owned persistent profile directories. Existing Chrome cookies are never copied.
- **Control coordinator:** single owner for user/agent input state, focus leases, cancellation, and handback.
- **Browser adapter:** versioned DOM/ARIA reads and actions with navigation-origin checks.
- **Windows broker:** strict typed requests for process, PowerShell, UI Automation, foreground transfer, and approved file access.
- **BOSS:** checkpointed task graph, deterministic-first capability lookup, evidence gates, specialist role configurations, budgets, and recovery.
- **Inference router:** current provider capabilities discovered from official APIs/docs plus benchmarked local inference.
- **Storage:** SQLite metadata, content-addressed artifacts, private vector index, separate signed global replica, append-only local event journal.
- **Media/document services:** safe parsers, OCR, export validators, FFmpeg, PDF rendering, local transcription, image/video-frame analysis.

## Dependency direction

UI and adapters depend on stable contracts. Security policy is evaluated before broker execution. Provider and server implementations are replaceable adapters. Untrusted content never becomes executable instructions without a separate policy decision and an explicit typed action.

## Initial repository map

- `src/DCx.Desktop` — native Windows shell.
- `src/DCx.Core` — provider-neutral policies and domain contracts.
- `tests` — deterministic unit/integration/UI/security/accessibility tests.
- `server/DCx.GlobalService` — P09 SQLite/Kestrel authoritative-service simulation; production deployment remains disabled.
- `docs/progress` — phase evidence and blockers.
- `DCx_Codex_Handoff_Kit` — authoritative product and gate definitions.
