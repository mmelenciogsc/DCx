# P14 — Pilot and release readiness

- Status: BLOCKED / NO_GO (local readiness controls pass; pilot and production activation are off)
- Implementation commits: `01539fd`, `66715b0`, `edd516a`
- Documentation commit: recorded by the commit containing this file

No pilot was started, no telemetry was collected, no user was enrolled, and no production service or payment was activated.

## Implemented

- Deterministic release-readiness evaluator with `GO` and `NO_GO` decisions.
- `GO` requires every critical/major issue to be validated and explicit production-activation approval. Approval alone cannot override a blocker.
- Every issue requires a unique ID, owner role, next action and evidence reference; missing triage fails closed.
- Privacy-minimized catalog of 12 pilot metrics covering completion, timing, token usage, tested CCB reuse, retrieval, crashes, user control, false claims, accessibility, sync bandwidth and CARE variance.
- Local SQLite aggregate store defaults off and records only cataloged metric/day/enum-dimension aggregates after versioned consent. It has no raw-event, prompt, output, URL, path, account or user-identifier column.
- Ratio samples are restricted to zero/one; route, error, claim and accessibility dimensions are closed enums rather than caller-provided strings. Other metrics permit only the aggregate dimension.
- Consent notice versions cannot roll back. Opt-out atomically disables recording and purges existing local aggregates.
- Atomic JSON readiness report utility and native validation script.
- Seven current critical/major blockers are assigned to owner roles with next actions and evidence.
- Native keyboard- and UIA-accessible read-only readiness dashboard launched from the main window or with `DCx.exe --readiness`.
- The JSON report and native dashboard consume the same immutable current-readiness policy, preventing blocker-list drift. The dashboard exposes no production-activation action.
- Pilot and incident-response runbooks describe opt-in, stop conditions, data minimization, escalation and rollback.

## Reproduction

```bash
cd /mnt/d/DCx
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p14.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P14 release/readiness tests: passed 14, failed 0, skipped 0; about 1 second.
- Current expanded full suite: passed 151, failed 0, skipped 0; about 2 seconds.
- Critical/major issues triaged: 7/7 (100%).
- Blocking critical/major issues: 7.
- Privacy-minimized pilot metrics: 12.
- Production activation approved: false.
- Decision: `NO_GO`.
- Aggregate-store tests use only synthetic local values; no pilot event was collected and no network transport exists.
- Native UIA exposed the `NO GO` decision, 7-blocker/12-metric summary, default-disabled consent state, blocker list, metric-definition list and refresh action. The validator confirmed that no control named `Activate production` exists.
- Evidence: `artifacts/test-results/p14-e72be5b1edb9457789270ce65511b507/go-no-go.json` (generated and intentionally not committed).

## Changed files in the latest milestone

- Shared policy and tests: `src/DCx.Core/Release/ReleaseReadiness.cs`, `tests/DCx.Core.Tests/Release/ReleaseReadinessTests.cs`, `tests/DCx.ReadinessReport/Program.cs`.
- Native accessible UI: `src/DCx.Desktop/ReadinessWindow.xaml`, `src/DCx.Desktop/ReadinessWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`.
- Reproducible UIA gate: `scripts/validate-p14.ps1`.
- Windows fixture cleanup was separately stabilized in `1c06d97`; three consecutive no-build runs and the P14 checkpoint CI run each passed 146/146.

## Blockers

- P06 transcription/embedding backend gate, P09 Compose gate, P10 hostile CCB sandbox, P11 PayPal sandbox/eligibility, P12 live/human acceptance and P13 signed installer/security/human-AT gates are unresolved.
- Privacy notice, terms, CARE/commission policy, provider distribution approvals and legal/compliance review are not approved.
- The readiness dashboard is local and read-only; it is not a pilot admin console. No enrollment system, approved accessible consent/withdrawal UI, network metric transport, support staffing or incident exercise exists. The local versioned consent record and aggregate store are not wired to product event collection.
- A staged pilot requires explicit owner approval after blocker closure. Production activation remains off by construction in this report.
