# DCx UI continuation: U00–U18 progress matrix

Updated: 2026-10-22. This matrix is additive to the historical P00–P14 ledger in `docs/progress/STATUS.md`; it neither upgrades nor invalidates those claims.

| Phase | Status | Evidence / current truth | Next safe gate |
|---|---|---|---|
| U00 Baseline audit | PASS (audit only) | Fresh isolated clone, SHA reconciliation, toolchain/license/secret-risk inventory, architecture audit, this matrix, handoff, and ADR 0122. | Review the U00 documentation diff; reopen only if baseline changes. |
| U01 Screen-reader contract | NOT_STARTED | P01–P03 provide native automated UIA/keyboard evidence, but the new browser-first semantic component contract and continuation harness do not exist. | Define requirement-to-test IDs and a semantic HTML/WebView2 component contract without claiming human AT results. |
| U02 Installed shell/container readiness | NOT_STARTED | Existing WPF shell and WebView2 prerequisite policy are native-only; the Debian/Docker managed continuation shell has not been designed or tested. | Produce a bounded compatibility design and failure-dialog test plan; retain installer/manual gates blocked. |
| U03 Shared browser/session proof | NOT_STARTED | Historical P02 fixture proves a controlled local handoff, not a Docker-backed live shared authenticated session. | Prototype one real WebView2-visible origin with typed host IPC and a manual-login/takeover proof before broader UI work. |
| U04 New tab and agent panel | NOT_STARTED | Existing native controls do not evidence the new tab/omnibox distinction, resizable browser-first panel, or wired controls. | Implement only after U03 architecture proves the shared session. |
| U05 Files/documents/media | NOT_STARTED | P07 has bounded local fixture evidence, not the requested browser-first continuation surfaces. | Map existing safe document/media contracts into semantic UI components and preserve existing P07 regressions. |
| U06 Windows-app handoff | NOT_STARTED | P03 automated native broker/handoff evidence exists, but no continuation host bridge/foreground workflow is proven. | Define typed, origin-bound handoff contract and native foreground/manual test harness. |
| U07 Advanced workspace | NOT_STARTED | P04–P11 contain local policies and simulations; no continuation advanced UI/provenance/earnings surface exists. | Reuse existing policy contracts only through least-privilege read models. |
| U08 Visual design tokens | NOT_STARTED | Existing WPF UI evidence is not a light/dark/high-contrast/reflow continuation design system. | Define tokens, contrast/reflow tests, and non-colour selected-state requirements. |
| U09 Security and stability | NOT_STARTED | Existing threat model is valuable but does not cover a Docker-backed WebView2 bridge. | Threat-model typed IPC, origin isolation, browser prompt injection, volume/mount policy, and host action approvals. |
| U10 Full acceptance / RC | BLOCKED | Depends on U01–U09 and mandatory human AT/clean Windows installer evidence. | Maintain a blocked release checklist; no release claim. |
| U11 Hardware adaptive compute | NOT_STARTED | P06 Windows inventory/owned-Ollama evidence is useful, but Windows/WSL/container distinction and continuation UI/resource manager are absent. | Inventory contract with CPU-only mocks and truthful unknown/unavailable states. |
| U12 CUDA/WSL2/Docker inference | NOT_STARTED | No continuation Docker GPU passthrough or actual in-container inference evidence. | Owner-approved, isolated prerequisite and container proof; no driver change. |
| U13 Peripheral bridge/devices UI | NOT_STARTED | No typed authenticated Windows–Debian device bridge or Devices & Permissions UI. | Implement minimal versioned operations with separate user/agent grants and unsupported results. |
| U14 Peripheral E2E | BLOCKED | Requires physical devices, explicit consent, and real desktop/manual validation. | Build mocks first; record physical tests as BLOCKED until performed. |
| U15 Consolidated regression/installer | BLOCKED | Requires U02/U09/U11–U14 and clean Windows/AT results. | Preserve existing P00–P14 validators and add continuation regressions only after each slice. |
| U16 Ubuntu/Windows pipeline | IN_PROGRESS | Git/Windows handoff is reconciled and documented. Ubuntu has Docker but lacks the pinned .NET 10.0.401 SDK; no remote build was rerun in this continuation. | Obtain approval for a reproducible SDK-10 provisioning plan, then add source-controlled cross-host scripts and checksum-bound Windows evidence. |
| U17 Create App | NOT_STARTED | No Create-App semantic surface, independent-project pipeline, or end-to-end Windows/Debian fixture exists. | Start with a least-privilege plan/retrieval/manifest contract and a sandboxed Debian CSV fixture; native Windows package/manual gate remains blocked. |
| U18 Business/global integration | NOT_STARTED | P07–P14 have bounded fixtures/simulations, not the new combined acceptance. | Reuse only existing mock/sandbox contracts; no production provider, financial, account, or contribution operation. |

## Inherited phase facts

P00–P05, P07, and P08 have recorded automated/local gates. P06 and P09–P14 remain partial or blocked. The most recent recorded native Windows validation is 701/701 at source `7ff6387`; its descendants through `d5e4477` contain documentation/continuation records. See `docs/progress/STATUS.md`, `docs/progress/COMPLETION-AUDIT.md`, and the individual `PHASE-XX.md` reports for exact scope.

## Release rule

Any critical physical, installer, accessibility, device, or live-provider gate marked BLOCKED means DCx is not release-ready. Automated Playwright, axe, UIA, SSH, mock, or fixture success never substitutes for a human JAWS/NVDA/Narrator result.
