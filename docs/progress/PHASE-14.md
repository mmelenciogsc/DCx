# P14 — Pilot and release readiness

- Status: BLOCKED / NO_GO (local readiness controls pass; pilot and production activation are off)
- Product implementation commit: `9745e33`
- Evidence-retention/validation commit and current exact source: `7f6e707`
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
- Native keyboard- and UIA-accessible readiness dashboard launched from the main window or with `DCx.exe --readiness`; the release decision, blockers and metric definitions remain read-only.
- The JSON report and native dashboard consume the same immutable current-readiness policy, preventing blocker-list drift. The dashboard exposes no production-activation action.
- A separate local privacy group displays the complete minimized-data notice and current daily-aggregate bucket count. Enabling requires an initially unchecked one-use consent; stopping and permanent purge require a separate initially unchecked one-use acknowledgment. Both results receive focus, five access keys are distinct, and actions are mutually excluded while running.
- Local opt-out atomically disables recording and purges all aggregates even when an older client supplies an earlier notice version; the highest recorded notice version is preserved rather than rolled back. Enabling still rejects notice-version rollback.
- These controls connect no product event source, network transport, pilot enrollment, export, production action or activation authority.
- Pilot and incident-response runbooks describe opt-in, stop conditions, data minimization, escalation and rollback.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p14.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P14 exact-namespace release/readiness tests: passed 11, failed 0, skipped 0. TRX counters replace the former broad substring selector that accidentally admitted unrelated test names.
- Current exact-source expanded full suite: passed 562, failed 0, skipped 0; native Windows with zero build warnings/errors.
- Critical/major issues triaged: 7/7 (100%).
- Blocking critical/major issues: 7.
- Privacy-minimized pilot metrics: 12.
- Production activation approved: false.
- Decision: `NO_GO`.
- Aggregate-store tests use only synthetic local values; no pilot event was collected and no network transport exists.
- Native UIA exposed the `NO GO` decision, 7-blocker/12-metric summary, default-disabled/zero-bucket consent state, blocker list, metric-definition list and refresh action. It proved separate initially unchecked opt-in and stop/purge approvals, successful local enable, focused completion, approval reset, successful stop/purge back to zero buckets, focused purge completion and five distinct access keys. The validator confirmed that no control named `Activate production` exists.
- The P11 readiness gate was rerun after composing the default-off contributor process transport with the native dashboard under a separate validation-only startup gate. It proves a read-only digest-authenticated token-to-subject mapping, operator/contributor role separation, hardened literal-loopback transport, real ledger projection and native focus/keyboard behavior. P11 remains blocking because this is not production contributor identity, MFA/TLS/credential rotation/multi-subject authorization/deployment, and production close/adjustment/hold/dispute/tax authority, exact plan/product configuration, credentials, merchant/Payouts and recipient eligibility, approved provider plus closure/requeue policy, centralized audit, deployed distributed fencing, external alert delivery/human acknowledgment/escalation and authorized provider acceptance remain absent.
- Evidence: `artifacts/test-results/p14-b82cf85071ef4d4ba449b44e2f20a91a/`, generated from exact source `7f6e707` and intentionally not committed. It contains `go-no-go.json`, `p14-readiness.trx` and the retained `native-readiness-uia.json` consent/focus/access-key/privacy-boundary transcript. The exact-source full suite passes 562/562 with zero build warnings/errors.

## Changed files in the latest milestone

- Local-only consent state/count and rollback-safe withdrawal: `src/DCx.Core/Release/PilotMetricAggregateStore.cs` and `tests/DCx.Core.Tests/Release/PilotMetricAggregateStoreTests.cs`.
- Accessible local privacy controls, stable statuses and focus: `src/DCx.Desktop/ReadinessWindow.xaml` and `src/DCx.Desktop/ReadinessWindow.xaml.cs`.
- Native approval/enable/stop/purge/focus/access-key/activation-absence gate and retained machine-readable UIA transcript: `scripts/validate-p14.ps1`.
- Decision and human-AT boundary: `docs/architecture/adr/0087-local-only-pilot-metric-consent-controls.md` and `docs/accessibility/P14_PILOT_PRIVACY_CHECKLIST.md`.
- Shared policy and tests: `src/DCx.Core/Release/ReleaseReadiness.cs`, `tests/DCx.Core.Tests/Release/ReleaseReadinessTests.cs`, `tests/DCx.ReadinessReport/Program.cs`.
- Native accessible UI: `src/DCx.Desktop/ReadinessWindow.xaml`, `src/DCx.Desktop/ReadinessWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`.
- Reproducible UIA gate: `scripts/validate-p14.ps1`.
- Windows fixture cleanup was separately stabilized in `1c06d97`; three consecutive no-build runs and the P14 checkpoint CI run each passed 146/146.

## Blockers

- P06 transcription/embedding backend gate, P09 Compose gate, P10 production CCB isolation/deployment gate, P11 exact-plan provisioning/credentialed PayPal acceptance/provider eligibility and verification/closure/requeue-policy/authenticated-transport/distributed-fencing/production-scheduling/external-notification gates, P12 live/human acceptance and P13 signed installer/security/human-AT gates are unresolved.
- Privacy notice, terms, CARE/commission policy, provider distribution approvals and legal/compliance review are not approved.
- The readiness dashboard now has automated evidence for accessible local aggregate-consent enable and withdrawal/purge, but it is not a pilot admin console or approved pilot enrollment flow. The notice and interaction still require human JAWS/NVDA/Narrator and legal/privacy approval. No enrollment system, product event source, network metric transport, final retention policy, support staffing or incident exercise exists.
- A staged pilot requires explicit owner approval after blocker closure. Production activation remains off by construction in this report.
