# DCx implementation status

Updated: 2026-10-17

DCx is under construction. It is not a validated release, installer, production service, or approved financial system.

| Phase | Status | Evidence summary |
|---|---|---|
| P00 Audit and bootstrap | Complete (local gate) | Locked native Windows Release build: 0 warnings/errors; 14/14 unit cases; 5/5 required UIA elements; milestone `a98195a`. Remote workflow is present but unrun because no remote exists. |
| P01 Native shell + accessible browser | Complete (automated native gate) | 21/21 unit cases; real HTTPS WebView; tabs, profiles, bookmarks, history, controlled download, keyboard and UIA flow pass at `d12fc64`. Human JAWS/NVDA/Narrator checklist remains explicitly unverified. |
| P02 Agent/browser shared control | Complete (automated native gate) | Scoped leases, approval, navigation renewal, DOM/ARIA adapter, manual takeover/handback, focus restoration and privacy-safe action log pass at `a8ee196`; human AT checklist unverified. |
| P03 External native apps/terminal broker | Complete (automated native gate) | Typed allowlist, workspace/reparse containment, restricted PowerShell, tracked-process UIA, explicit approval/takeover/resume and focus restoration pass at `74b7be1`; human AT checklist unverified. |
| P04 OpenAI identity and Codex | Complete (offline/native mock gate) | Local JSONL app-server lifecycle, model discovery, API-key credential boundary, streaming, cancellation, redaction and reconnect pass at `8ba629c`; no real provider call. Commercial ChatGPT sign-in remains blocked pending OpenAI approval. |
| P05 BOSS orchestration | Complete (deterministic benchmark gate) | DAG validation, eight roles, deterministic-first/privacy-aware routing, evidence, retries, cancellation, time/token budgets and JSONL checkpoints pass at `ce7ea0e`/`53af804`; fixture metrics make no savings claim. |
| P06 Local models | BLOCKED (partial implementation) | Existing Ollama/Qwen3-VL 4B local CUDA vision benchmark passes at `463bfb6`; selection/fallback tests pass. No Whisper backend exists and install/download is not authorized, so transcription gate remains blocked. |
| P07 Files/documents/media | In progress | Starting safe file/media contracts and fixtures; P06's unavailable transcription backend will remain an explicit dependent limitation. |
| P08 Private CKB/CCB | Not started | No validated implementation. |
| P09 Global registry/sync | Not started | Docker daemon access unavailable to current WSL user; real Ubuntu deployment forbidden without approval. |
| P10 Contributions/governance | Not started | No validated implementation. |
| P11 PayPal/CARE | Not started | Sandbox only when implemented; live payouts disabled and eligibility unverified. |
| P12 Full vertical E2E | Not started | No validated implementation. |
| P13 Packaging/security/reliability | Not started | No installer or signing identity. |
| P14 Pilot/release readiness | Not started | Production activation off. |

## Environment facts affecting the program

- Windows 11 Home Single Language build 26200, x64; app is deliberately `asInvoker` despite the development account being an administrator.
- .NET SDK 10.0.401; WebView2 Runtime 155.0.4283.45.
- NVIDIA RTX 4060 Laptop GPU with 8,188 MiB measured VRAM; 16.87 GB host RAM; Intel i7-12650H.
- WSL development distro is Ubuntu 22.04.3, not the separate Ubuntu 24.04 server described by the owner.
- D: has about 120 GB free and is 88% used; large local-model/artifact downloads require explicit capacity checks.
- JAWS 2023–2025, NVDA 2026.1.1, and Narrator are installed. No human screen-reader pass has been recorded.
