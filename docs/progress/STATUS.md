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
| P06 Local models | BLOCKED (hardened partial implementation) | At `a160435`, 22 tests cover no-redirect exact-origin Ollama transport, image/response admission, execution-leased hash-pinned Whisper files, bounded adapters, low-resource selection and private-safe fallback. The installed Qwen3-VL 4B RTX benchmark and P07 UIA frame description pass. No real Whisper or embedding backend is installed/benchmarked, and download is not authorized. |
| P07 Files/documents/media | Complete (automated native gate) | 74/74 tests and native UIA pass at `c618254`: local content/media, pause-frame-describe-resume, Office extraction, scanned-PDF OCR/geometry, semantic repair, HTML/DOCX exports, hostile-input denial and safe YouTube handoff. Human AT remains unverified; YouTube frame capture and transcription are not claimed. |
| P08 Private CKB/CCB | Complete (automated local/native gate) | Embedded SQLite/FTS5 plus bounded vector retrieval, version/dependency schema, isolated profile stores, signed/digested artifacts, hostile ZIP rejection and tested BOSS CCB reuse pass in 79/79 full tests at `685ca5a`. |
| P09 Global registry/sync | BLOCKED (hardened partial implementation) | At `9123a81`, 9/9 signed-sync tests and actual loopback Kestrel/SQLite transport pass, including durable canonical publication-id replay/conflict protection across reopen in addition to trust rotation, resume, atomic activation, rollback, lazy artifacts and private isolation. Required Compose run and production key custody remain blocked. |
| P10 Contributions/governance | BLOCKED (hardened local orchestration) | At `9123a81`, 19/19 policy tests, native UIA and the real rootless Bubblewrap hostile-fixture gate pass. Exact schema-v2 quarantine/testing/evidence/signed propagation plus crash-safe approval/withdrawal release reconciliation work in the server fixture. The deployed service still exposes no contribution API or production executor; Ubuntu 24.04, auth, automatic recovery worker, seccomp/cgroup/image and external penetration gates remain. |
| P11 PayPal/CARE | BLOCKED (durable local sandbox) | At `4b05cc0`, 13/13 finance tests pass: restart-safe SQLite ledger/webhook/entitlement/payout state, balanced CARE allocation, Net60, sandbox-only payout gates and deterministic recovery for pre-send, provider-accepted and provider-pending crash states. Official PayPal adapter/sandbox verification, merchant eligibility and legal/accounting approval remain absent. |
| P12 Full vertical E2E | BLOCKED (eight local fixtures) | At `10ac274`, eight composed validators pass with zero production actions, including a clearly non-Facebook WebView2/UIA old-post fixture and sandbox-shaped data to genuine XLSX. Live Facebook, QuickBooks OAuth/API, YouTube frame Q&A, package install, Compose/production CCB deployment and real PayPal acceptance remain absent. |
| P13 Packaging/security/reliability | BLOCKED (hardened partial implementation) | At `3e9b172`, 14 reliability tests, published-app UIA closed-app backup/approved restore, runtime lease denial, two reproducible 42-file publish trees/ZIPs, CycloneDX/license inventory, secret scan and offline startup pass. Output is `NotSigned`; no installer, vulnerability/AV scan, clean-VM lifecycle or human AT signoff is claimed. |
| P14 Pilot/release readiness | BLOCKED / NO_GO (local controls) | At `edd516a`, 14 release tests plus native UIA pass; a read-only dashboard and JSON report share the same immutable 7-blocker policy. Recording is default-off, catalog/enum-only and aggregate-by-day with opt-out purge. The current full suite passes 163/163. Production activation is false; no pilot, event wiring, admin console or policy approval is claimed. |

## Environment facts affecting the program

- Windows 11 Home Single Language build 26200, x64; app is deliberately `asInvoker` despite the development account being an administrator.
- .NET SDK 10.0.401; WebView2 Runtime 155.0.4283.45.
- NVIDIA RTX 4060 Laptop GPU with 8,188 MiB measured VRAM; 16.87 GB host RAM; Intel i7-12650H.
- WSL development distro is Ubuntu 22.04.3, not the separate Ubuntu 24.04 server described by the owner.
- D: has about 120 GB free and is 88% used; large local-model/artifact downloads require explicit capacity checks.
- JAWS 2023–2025, NVDA 2026.1.1, and Narrator are installed. No human screen-reader pass has been recorded.
