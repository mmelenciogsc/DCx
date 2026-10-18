# P04 — OpenAI identity and local Codex integration

- Status: Complete (offline/native mock gate; external account invocation unverified)
- Implementation commits: `8ba629c`, `e559975`, `5873218`, `c200e8f`, `5614991`

Commercial ChatGPT sign-in for DCx distribution is a strict eligibility blocker. API-key mode remains independent.

## Official-contract findings

Checked official OpenAI documentation on 2026-10-17:

- Standard OpenAI API requests use bearer API credentials. API-key usage is separately billed through the Platform account.
- Codex app-server 0.146.0 uses newline-delimited JSON over stdio. A client sends `initialize`, then `initialized`, and uses `model/list`, `thread/start`, `turn/start`, streamed notifications and `turn/interrupt`.
- Generated schemas from the locally installed WSL CLI confirm the exact 0.146.0 request shapes used here. They were generated into `/tmp`, inspected, and were not added as product dependencies.
- `model/list` is a catalog, not proof of entitlement; DCx never hardcodes the resolver's current model result.
- Sign in with ChatGPT is a limited trial for selected commercial partners. The open-source token-sharing path explicitly directs paid or remotely hosted apps to an interest form. DCx therefore exposes a clear unavailable state and implements no commercial OAuth flow without approval.
- The ChatGPT-plan preview requires `store: false` and `stream: true` and has material feature restrictions. Those conditions are not treated as API-key rules.

See `docs/references/VERIFICATION_LOG.md` and ADR 0004 for source URLs and the resulting boundary.

## Implemented

- Provider-neutral local Codex JSONL client with process startup, strict initialization, request correlation, streaming deltas, model discovery, interruption, disconnect, bounded diagnostics and reconnect.
- Sanitized child environment allowlist. Production Codex receives a DCx-owned per-user `CODEX_HOME`; it does not reuse the developer's Codex profile.
- Production app-server configuration uses ephemeral Codex credential storage, API-only login enforcement, disabled web search/apps/multi-agent features, and no automatic secret forwarding to tool processes.
- API keys are stored in Windows Credential Manager and sent only through the local `account/login/start` protocol. They are never placed in source, settings JSON, command arguments or logs.
- Thread starts are ephemeral, read-only and non-interactive in this phase. App-server server-originated tool approval requests are not accepted.
- Runtime model names come from `model/list`; no production model identifier is hardcoded.
- Accessible OpenAI/Codex settings window with API-key save/delete, provider/runtime status, model picker, prompt, Run, Cancel and Disconnect controls.
- Permanently disabled commercial ChatGPT button until eligibility evidence changes the compiled distribution capability.
- Repository-owned Windows subprocess fixture that exercises the same JSONL transport without credentials, network access or billable inference.
- Cancellation rejects before `turn/start` dispatch or registers one pending intent against the exact unique ephemeral thread before sending. The first exact identity-bearing started, delta or completed notification binds the turn and permits immediate exact interruption; the later correlated response must identify the same thread/turn. Response-reader binding remains the consistency check and fallback. Conflicting response/notification identity, ambiguous mapping and cross-thread turn reuse fail closed.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\smoke-desktop.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p02.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p03.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p04.ps1
```

## Measured final results

Native Windows 11 build/test after final hardening:

- Locked restore and Release build: passed; 0 warnings, 0 errors; 2.52 seconds.
- Tests: passed 41, failed 0, skipped 0; about 1 second.
- Credential test wrote a unique dummy generic credential for the current Windows user, read it, deleted it and verified absence in a `finally` cleanup.
- Process integration proved initialize/initialized, one dynamically listed model, completed streamed text, interruption status, exact child shutdown and restart to a different process ID.
- A deliberately leaked fixture bearer secret was redacted from bounded diagnostics.
- P00 smoke and P01–P03 native UIA regressions passed.
- P04 UIA E2E passed: blocked ChatGPT state/reason, isolated test runtime, JSONL connection, one selected model, streamed response and explicit disconnect.
- Active-turn cancellation waits for the exact in-flight turn before requesting interruption, then proves the turn is removed. A later expanded-suite run exposed the remaining notification-before-response race; the fixture now forces that ordering, and the two P04 process tests passed six consecutive focused runs after identity retention was hardened. The current committed-source native validator is recorded with the encompassing milestone evidence.
- The first expanded pre-commit run after the P06 observer change passed 494/495: `Client_CancellationInterruptsOnlyActiveTurn` received `TaskCanceledException` while waiting for `turn/start`. The deterministic delayed-response fixture reproduced that ordering. After `5873218`, the exact-commit full suite passed 495/495 and `validate-p04.ps1` passed its native offline UIA flow. This non-zero observation remains part of the record.
- A later expanded run at the P12 milestone passed 550/551: `Client_CancellationInterruptsOnlyActiveTurn` observed a completed turn. The reader had completed the correlated response task, but its awaiting continuation could still be overtaken by the following terminal notification. Commit `c200e8f` binds the pre-registered cancellation intent synchronously in the protocol response reader before later notifications are read. Both focused process tests then passed together, followed by eight consecutive no-build focused runs, the native P04 UIA gate and the exact-source full suite at `02d0564` (551/551). This second non-zero observation remains recorded; ADR 0083 supersedes only the incomplete scheduling assumption, not ADR 0070's identity-retention rule.
- The first expanded run after the P06 inference-lease milestone passed 555/556: the same cancellation test again observed a completed turn. The fixture's delayed `turn/start` response blocked its single stdin loop, so an interrupt could remain unread until completion under load. More importantly, DCx ignored exact thread/turn identity already available from notifications while awaiting the response. Commit `5614991` registers cancellation by exact ephemeral thread before dispatch, binds the first identity-bearing turn notification, interrupts immediately, and treats the eventual response as a mandatory identity-consistency check. The fixture now processes delayed requests asynchronously and delays the response beyond normal uncancelled completion; a conflicting notification/response case is also denied. The three focused cases passed, then ten consecutive no-build focused runs, native P04 UIA and the exact-source full suite passed 557/557 with zero build warnings/errors. This third non-zero observation remains recorded; ADR 0085 supersedes only the response-first assumption in ADRs 0070/0083.
- Secret-pattern filename scan found no matches outside ignored build/test artifacts.

## Changed files

- Protocol/access/security contracts: `src/DCx.Core/OpenAI`, `src/DCx.Core/Security/WindowsCredentialVault.cs`.
- Native runtime discovery and accessible provider UI: `src/DCx.Desktop/OpenAI`, `src/DCx.Desktop/OpenAiControlWindow.xaml*`, shell entry controls.
- Offline process fixture: `tests/DCx.FakeCodexAppServer`.
- Unit/integration tests: `tests/DCx.Core.Tests/OpenAI`, `tests/DCx.Core.Tests/Security`.
- Turn-identity cancellation ordering: `src/DCx.Core/OpenAI/CodexAppServerClient.cs`, the full-duplex delayed-response fixture and focused tests, `docs/architecture/adr/0070-retain-codex-turn-identity-through-cancellation.md`, the response-reader binding in ADR 0083, and notification-first exact binding in ADR 0085.
- Native UIA test: `scripts/validate-p04.ps1`.
- Human checklist: `docs/accessibility/P04_OPENAI_CHECKLIST.md`.

## Known limitations and blockers

- No Windows `codex.exe` is currently installed or discoverable in the native Windows environment. WSL Codex 0.146.0 was used only for contract/schema inspection; WSL is not an end-user dependency.
- No user API key was read, no real OpenAI request was sent, and no billable inference was performed. A real test requires the owner's explicit authorization.
- Commercial ChatGPT sign-in is BLOCKED pending documented OpenAI approval/eligibility for DCx distribution. No browser cookies or existing ChatGPT session data are reused.
- The UI supports a single active provider connection/turn. Broader lifecycle orchestration and budgets begin in P05.
- Human JAWS/NVDA/Narrator validation remains unperformed.
