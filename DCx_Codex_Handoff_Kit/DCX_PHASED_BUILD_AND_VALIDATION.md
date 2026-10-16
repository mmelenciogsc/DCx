# DCx phase-by-phase execution plan and release gates

**Rule of engagement:** Work in order. Before each phase: read requirement, inspect existing repository and dependencies, create plan/tests and note assumptions. After each: execute tests, record exact commands and results in `docs/progress/PHASE-XX.md`, include changed files and SHA/commit, bugs/limitations, security and accessibility status. Gate = green only with executable evidence. Stop and report when human credentials/approval or unavailable environment blocks; continue independent work; never fabricate passes. `docs/progress/STATUS.md` must indicate complete/partial/blocked per phase. Preserve build resumption instructions.

### P00 — Audit and bootstrap
Check WSL distribution, Windows version, user privileges, CPU/GPU/RAM/disk, .NET SDK and Windows build tools, Git, npm, Codex version, available UI test tooling. Inspect cwd for existing DCx repo and preserve work; use a clean `DCx` directory if absent. Baseline repo, `AGENTS.md`, solution architecture, ADRs, `.editorconfig`, license/notice, CI, reproducible builds, threat model, test fixtures. **Gate:** Windows and WSL interop commands with real output, CI green, documented architectural decision and reproducible local build instructions.

### P01 — Native shell + accessible browser
Build Windows 11 client, WebView2 interactive tabs and address navigation, history/downloads, persistent isolated browser profiles, accessible controls, keyboard shortcuts and agent drawer. Do not copy Chrome credentials. Add screen-reader-compatible live region and explicit focus control. **Gate:** Native Windows binary launches, real HTTPS site opens, user can tab/activate controls and use login field directly, automated UI tests and manual NVDA/JAWS/Narrator checklist with unverified entries marked.

### P02 — Agent/browser shared control
Browser DOM/ARIA read and action adapter, navigation state, safe downloads, user takeover and agent resume, action overlays/logging, accessible confirmation. Test dynamic feed focus restoration, site navigation and simulated authentication with test site. **Gate:** Repeatable demo of agent navigating, user manually completing a field, agent resuming; no conflicting typing, no involuntary focus theft, security tests.

### P03 — External native apps/terminal broker
Restricted Windows PowerShell/process/UIA adapters; external foreground app, human takeover and return to DCx, workspace allowlists, argument escaping, elevation/install prompts. Prefer supported application APIs over pixel clicks. **Gate:** Demonstrate Notepad/Calculator or demo Windows app control with UIA, foreground transfer and keyboard-only manual operation; destructive-operation denial tests.

### P04 — OpenAI identity and Codex integration
Run a per-user **local** Codex CLI/app-server subprocess; use supported structured protocol, health/cancel/reconnect; OpenAI API-key mode and approved eligible ChatGPT sign-in only if permitted for DCx distribution. Explicitly verify entitlement/auth docs. OpenAI API key stored locally protected and never uploaded. **Gate:** Mock integration tests; one real approved sandbox/test invocation only when authorized; graceful unavailable sign-in path, token redaction and cancellation.

### P05 — BOSS orchestration and specialist roles
Implement task DAG/checkpoints, planner/executor/reviewer/validator, deterministic-first capability lookup, routing policy and capability checks, token budgeting, cancellation, retries, evidence gates, per-task privacy controls and process limits. **Gate:** Bench suite of deterministic vs model-assisted tasks showing accurate completion, bounded retries, routing decisions and recorded tokens/latency; no hardcoded claims of savings.

### P06 — Local models and installer resource inventory
Hardware profiling, Ollama availability/install approval, compatible VLM/embedding/quantized models chosen by benchmark, Whisper backend outside Ollama if needed, licenses/hashes, runtime memory budget and unload, CPU/cloud fallback. **Gate:** hardware manifest, permitted install tests, transcription fixture accuracy, image Q&A fixture, bounded RAM/VRAM and offline performance; low-resource emulation.

### P07 — Files, documents, accessibility and media
Browse local folders; supported PDF/docx/xlsx/pptx/csv/txt/images/audio/video viewers; scanned-PDF wizard with spatial field validation; VLM descriptions of images/frames; safe video pause/resume; semantic repair overlay for unlabeled icons/tables. **Gate:** Accessibility tree tests, real artifact exports, OCR/visual fixture goldens, YouTube/stream limitations reported; no display-only exports presented as editable.

### P08 — Private local CKB/CCB
Install Qdrant or selected embedded vector index and SQLite metadata store; local artifact store, version and dependency schema, sparse/hybrid retrieval if needed, signed code provenance, reproducible CCB tool test harness. **Gate:** local insert/search/version conflict/private isolation tests, malformed or malicious artifact rejection, offline retrieval, fixtures and tested capability reuse.

### P09 — Global registry and client synchronization (locally hosted simulation first)
Server service and DB for curated CKB/CCB, signed incremental release manifests and checksums, checkpointed sync and atomic swaps, local global replica distinct from user private, artifact download-on-use, conflict/deprecation/rollback and offline handling. Deploy local docker-compose simulation first. **Gate:** two simulated clients update from authoritative server, corrupt signature rejected, interrupted delta recovery, private collections unchanged; document bandwidth/storage forecasts. Do not deploy to real Ubuntu server without permission.

### P10 — Contribution, governance and provenance
Unchecked opt-in, per-contribution review, PII/license/secret scanner, provenance/ownership attestations, sandbox build/test, approval/quarantine, derivative module credit DAG, withdrawal/deprecation, replay-proof-ish usage receipts (untrusted client threat), anomaly detection. **Gate:** unconsented upload impossible; malicious/dummy submission quarantined; approved resource propagated; fraudulent receipts excluded; no private data in telemetry.

### P11 — PayPal subscriptions and CARE
$3/mo plan, sandbox APIs, webhook signature verification/replay/idempotency, entitlement state, configurable net-profit accounting, aggregate contributor allocation <= 40% of *positive* distributable net profit, earned month UTC, Net60 = 60 calendar days after month close, thresholds/holds/refunds, payout batch preview + sandbox test, audit ledger. **Gate:** sandbox happy/failure/refund/chargeback test suite, double-entry reconciliation, zero/negative net no payout, attribution splits, no duplicate payout, live payouts completely disabled. Verify merchant feature eligibility and legal review blockers.

### P12 — Full vertical end-to-end flows
Automate Facebook search with test pages first; real site only with user’s session/authorization and honest site limitations; QuickBooks sandbox -> XLSX; URL -> inaccessible PDF wizard -> saved output; YouTube/local video frame Q&A; native application handoff; package/install command approval; CKB/CCB search and reuse; contributor receipt and sandbox financial ledger. **Gate:** each scenario has exact reproducible steps, test fixtures and captured pass/fail; separate human acceptance tests for live services.

### P13 — Packaging, security and reliability
Native Windows installer (MSIX/MSI selection justified), code signing pathway, WebView2 runtime bootstrap, user-mode install/uninstall/update, AV/SBOM/license checks, accessibility audits, memory/cost budgets, controlled updater, crash reports with privacy safeguards, backup/restore/rollback, reproducible release assets. **Gate:** clean Win11 VM installation/uninstallation, startup offline and online, secure upgrades, no bundled secrets, automated security scans, human assistive-technology test signoff.

### P14 — Pilot and release readiness
Staged opt-in pilot, metrics for completion, time, token usage, re-use ratio, successful retrieval, crash rate, user control, false agent claims, accessibility failures, server sync bandwidth, CARE reconciliation; technical runbooks, incident response, admin console, privacy/terms/commission policy, PayPal/OpenAI approvals and platform distribution compliance. **Gate:** explicit report of validated features vs deferred/blocked, 100% major issues triaged, test evidence, go/no-go decision. No production activation by default.

## Hard quality gates (cross-cutting)
- All public UI controls keyboard operable; visible focus, UIA naming/roles, stable reading location and screen-reader announcements; manual JAWS/NVDA/Narrator testing must be honestly distinguished from automated tests.
- Permission and prompt-injection test suites before executing third-party code, installs, file deletion, payments, publication, or OS privilege changes. User can cancel/revoke.
- No cloud upload of user's private local CKB/CCB by default; opt-in and item-by-item approval; only local signed replica updates.
- Unit + integration + E2E + negative tests, deterministic fixtures, evidence logs, CI on actual Windows for Windows features. Linux/WSL results alone never satisfy native GUI acceptance.
- Financial and security policy are subject to human approval and testing; simulated passes must never be mislabeled production-ready.
