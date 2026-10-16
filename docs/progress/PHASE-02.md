# P02 — Agent/browser shared control

- Status: Complete (automated native-Windows gate; human assistive-technology rows unverified)
- Commit: `a8ee196`

Required gate: repeatable agent/manual-user/agent handoff with focus restoration, no conflicting input, and security tests.

## Initial plan and assumptions

Implement an explicit control-state machine (`User`, `AgentPending`, `Agent`, `UserTakeover`, `Paused`, `Cancelled`) before enabling any browser action. DOM/ARIA reads and actions will use typed same-WebView contracts, origin/navigation-version checks and per-action evidence. Any physical user input revokes the agent input lease. The controlled fixture will simulate authentication: agent navigates, user types the approval field manually, then explicitly hands control back for the agent to continue. No real credentials or Facebook automation are used in the P02 CI path.

## Implemented

- Thread-safe browser-control coordinator with explicit pending/agent/navigation-pause/user-takeover/cancel/user modes.
- Five-minute action leases bound to request, tab ID, approved origin and navigation generation.
- Cross-origin, wrong-tab, stale-generation, missing, expired/unapproved-state actions deny by default.
- Approved navigation revokes the old lease and issues a new one only after completion on the approved origin.
- Fixed-script DOM adapter exposes control names/roles without input values; actions are limited to focus/click/read-value by element ID. Password values are explicitly unreadable.
- Page key/pointer capture messages can only revoke agent control; untrusted pages cannot grant it.
- Accessible approval group, assertive control/status instructions, Take control, Hand back, Cancel and bounded provenance log.
- Deterministic loopback-only handoff demo: agent navigation/inspection → user field entry → explicit handback → validation/click → completion evidence → user focus restoration.
- P02 manual assistive-technology checklist with every unperformed row marked UNVERIFIED.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p01.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p02.ps1
```

## Measured final results

Combined native Windows 11 regression on build 26200.9457:

- Locked build: passed; 0 warnings, 0 errors; 2.16 seconds.
- Core tests: passed 27, failed 0, skipped 0; 61 ms.
- P01 regression: passed.
- P02 UIA E2E: passed.
  - Accessible approval group exposed.
  - Agent paused in User takeover mode before field entry.
  - `Approval code` received a non-secret test value only during user control.
  - Hand back required explicit activation.
  - Page exposed `Agent resumed after user handback.` as completion evidence.
  - Keyboard focus returned to `Approval code` after completion.
  - Action log exposed 6 ordered entries and did not contain the user field value.
  - Final mode: User.
- The P02 automation was also run twice consecutively after a readiness-race fix; both runs passed.

## Security tests and observations

- Unit tests prove no action before approval; wrong tab, origin and page generation are denied; cross-origin navigation is denied; physical input revokes; handback issues a distinct lease; rejection creates no lease; completion clears it.
- The E2E target must be loopback. No general external-site agent execution is exposed by this phase.
- The first combined run revealed a readiness race: the script could request control before the initial WebView existed. Validation now explicitly waits for browser readiness and repeat passes confirm the fix.
- Programmatic page clicks do not generate the physical pointer event used for user revocation. Real page key/pointer messages only reduce privilege by returning control to the user.
- Dynamic live text now exposes its actual message as the accessible name instead of a fixed generic label.
- No credentials, API calls, external login or field value logging occurred.

## Changed files

- Control contracts/state machine: `src/DCx.Core/Control`.
- DOM contracts/adapter: `src/DCx.Core/Automation`, `src/DCx.Desktop/Automation`.
- Shared-control UI and lifecycle: `src/DCx.Desktop/MainWindow.xaml*`.
- Security tests: `tests/DCx.Core.Tests/Control`.
- Dynamic controlled fixture: `tests/DCx.WebFixtureServer/Program.cs`.
- Native UIA E2E: `scripts/validate-p02.ps1`.
- Human checklist: `docs/accessibility/P02_HANDOFF_CHECKLIST.md`.

## Known limitations carried forward

- This is a deterministic safety integration, not an LLM/Codex agent. OpenAI/Codex integration remains P04.
- Human JAWS/NVDA/Narrator behavior and actual physical interruption are unverified; they remain release blockers despite UIA coverage.
- P02 does not automate a real authenticated website. The Facebook flow is reserved for P12 with manual login and site-policy fallback.
- Generalized browser action schemas, richer ARIA relationships and download/content approval continue in later phases.
