# P14 — Pilot and release readiness

- Status: BLOCKED / NO_GO (local readiness controls pass; pilot and production activation are off)
- Implementation commits: `01539fd`, `66715b0`, `edd516a`, `26da605`, `993149e`, `54f66bc`, `9877a60`, `faf6b71`, `1631ebb`, `599cee9`, `ec37d1f`, `a662abb`, `8583b29`, `d5624a6`, `9576d63`, `261f6ee`, `76d0750`, `562667b`, `f66dd20`, `3bf9452`, `23ed1d9`, `9140183`, `8cb2db4`, `eb81ac3`, `66c4c8c`, `05345b9`, `0c72488`, `70f8dd7`, `abd91c0`, `7829af1`, `8783ed2`, `7df8c0a`, `84de38b`, `1bb4d7e`, `efc607c`, `0381f5e`, `b7d281e`
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
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p14.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P14 exact-namespace release/readiness tests: passed 10, failed 0, skipped 0; about 1 second. TRX counters replace the former broad substring selector that accidentally admitted unrelated test names.
- Current committed-source expanded full suite: passed 433, failed 0, skipped 0; about 8 seconds under the repository-mandated native Windows PowerShell wrapper.
- Critical/major issues triaged: 7/7 (100%).
- Blocking critical/major issues: 7.
- Privacy-minimized pilot metrics: 12.
- Production activation approved: false.
- Decision: `NO_GO`.
- Aggregate-store tests use only synthetic local values; no pilot event was collected and no network transport exists.
- Native UIA exposed the `NO GO` decision, 7-blocker/12-metric summary, default-disabled consent state, blocker list, metric-definition list and refresh action. The validator confirmed that no control named `Activate production` exists.
- The P11 readiness gate was rerun after opaque exact-review blocked-case dispositions were implemented. The local accessible workflow now passes while structurally leaving events blocked and making zero provider/financial calls. P11 remains blocking because exact plan/product configuration, credentials, merchant/Payouts and recipient eligibility, approved provider plus closure/requeue policy, real operator identity/MFA/TLS/secret custody/centralized audit, deployed distributed fencing, external alert delivery/human acknowledgment/escalation and authorized provider acceptance remain absent.
- Evidence: `artifacts/test-results/p14-af424454bb334b52bcc72fc07a26d226/go-no-go.json` with `p14-readiness.trx` beside it, generated from the exact source committed through `b7d281e` and intentionally not committed.

## Changed files in the latest milestone

- Shared policy and tests: `src/DCx.Core/Release/ReleaseReadiness.cs`, `tests/DCx.Core.Tests/Release/ReleaseReadinessTests.cs`, `tests/DCx.ReadinessReport/Program.cs`.
- Native accessible UI: `src/DCx.Desktop/ReadinessWindow.xaml`, `src/DCx.Desktop/ReadinessWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`.
- Reproducible UIA gate: `scripts/validate-p14.ps1`.
- Windows fixture cleanup was separately stabilized in `1c06d97`; three consecutive no-build runs and the P14 checkpoint CI run each passed 146/146.

## Blockers

- P06 transcription/embedding backend gate, P09 Compose gate, P10 production CCB isolation/deployment gate, P11 exact-plan provisioning/credentialed PayPal acceptance/provider eligibility and verification/closure/requeue-policy/authenticated-transport/distributed-fencing/production-scheduling/external-notification gates, P12 live/human acceptance and P13 signed installer/security/human-AT gates are unresolved.
- Privacy notice, terms, CARE/commission policy, provider distribution approvals and legal/compliance review are not approved.
- The readiness dashboard is local and read-only; it is not a pilot admin console. No enrollment system, approved accessible consent/withdrawal UI, network metric transport, support staffing or incident exercise exists. The local versioned consent record and aggregate store are not wired to product event collection.
- A staged pilot requires explicit owner approval after blocker closure. Production activation remains off by construction in this report.
