# P14 — Pilot and release readiness

- Status: BLOCKED / NO_GO (local readiness controls pass; pilot and production activation are off)
- Product implementation commits: local consent `9745e33`; strict local QuickBooks XLSX `da4187a`; tagged flattened PDF `3b35701`; aspect-preserved visual layer `1528e60`; changed-source denial gate `b9c4cad`; local pilot administration preview `4ff1a38`; staged cohort preview `98f2306`; accessible inert-update review `c506bb7`
- Evidence-retention commit: `7f6e707`; current exact validation source: `c506bb7`
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
- A separate local privacy group displays the complete minimized-data notice and current daily-aggregate bucket count. Enabling requires an initially unchecked one-use consent; stopping and permanent purge require a separate initially unchecked one-use acknowledgment. Both results receive focus, six access keys across privacy/administration/refresh are distinct, and actions are mutually excluded while running.
- A clearly labeled local pilot administration preview reports zero enrollment, product-event, network-metric-transport and activation controls; lists strictly revalidated aggregate buckets from the most recent 30 UTC days; and retains no raw event or remote state.
- Read-time aggregate admission rejects uncataloged keys, malformed days, undefined or metric-incompatible dimensions, invalid bounds, impossible sums, excessive sample counts and more than 10,000 results.
- A closed seven-scenario incident stop drill covers signing-key compromise, malicious global update, private-data exposure, inaccessible takeover, payout/reconciliation, updater rollback and backup/restore. It creates only an in-memory minimized stop/preserve/fresh-readiness plan, focuses the result and performs zero external actions.
- A closed ordered four-stage pilot preview covers owner-supervised rehearsal, assistive-technology, limited opt-in and expanded release-candidate cohorts. Each stage exposes its future audience plus entry/exit gate. The current state is `blocked-not-enrolled`; owner approval, cohort size and schedule are false/unapproved, and enrollment/event/transport/activation controls remain zero.
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
- P14 exact-namespace release/readiness tests: passed 17, failed 0, skipped 0. TRX counters replace the former broad substring selector that accidentally admitted unrelated test names.
- Current product expanded full suite at `c506bb7`: passed 667, failed 0, skipped 0; native Windows with zero build warnings/errors.
- Critical/major issues triaged: 7/7 (100%).
- Blocking critical/major issues: 7.
- Privacy-minimized pilot metrics: 12.
- Production activation approved: false.
- Decision: `NO_GO`.
- Aggregate-store tests use only synthetic local values; no pilot event was collected and no network transport exists.
- Native UIA exposed the `NO GO` decision, 7-blocker/12-metric summary, default-disabled/zero-bucket consent state, blocker/metric/recent-aggregate lists and refresh action. It proved separate initially unchecked opt-in and stop/purge approvals, successful local enable, focused completion, approval reset, successful stop/purge back to zero buckets, focused purge completion, six distinct access keys, the zero-authority administration boundary, four read-only future cohort list items and a focused seven-scenario incident dry-run. The validator confirmed that no control named `Enroll pilot`, `Send metrics` or `Activate production` exists.
- The P11 readiness gate was rerun after composing the default-off contributor process transport with the native dashboard under a separate validation-only startup gate. It proves a read-only digest-authenticated token-to-subject mapping, operator/contributor role separation, hardened literal-loopback transport, real ledger projection and native focus/keyboard behavior. P11 remains blocking because this is not production contributor identity, MFA/TLS/credential rotation/multi-subject authorization/deployment, and production close/adjustment/hold/dispute/tax authority, exact plan/product configuration, credentials, merchant/Payouts and recipient eligibility, approved provider plus closure/requeue policy, centralized audit, deployed distributed fencing, external alert delivery/human acknowledgment/escalation and authorized provider acceptance remain absent.
- Evidence: `artifacts/test-results/p14-c153f6de5ba948f0b6a82e373edc6efd/`, generated from exact source `c506bb7` and intentionally not committed. It contains `go-no-go.json`, `p14-readiness.trx` and schema-v3 `native-readiness-uia.json` consent/focus/access-key/privacy/administration/cohort/incident-boundary evidence. The current product expanded full suite passes 667/667 at the same source with zero build warnings/errors. The added controlled-update review remains inert and did not alter the seven blockers, `NO_GO` or production-activation false result.

## Changed files in the latest milestone

- Closed staged-cohort catalog and zero-authority blocked preview: `src/DCx.Core/Release/LocalPilotAdministrationPolicy.cs` and `tests/DCx.Core.Tests/Release/LocalPilotAdministrationPolicyTests.cs`.
- Read-only accessible stage/status surface: `src/DCx.Desktop/ReadinessWindow.xaml` and `src/DCx.Desktop/ReadinessWindow.xaml.cs`.
- Native four-item UIA and absent-authority evidence: `scripts/validate-p14.ps1`.
- Decision, security, privacy, accessibility and exact future plan: `docs/architecture/adr/0102-zero-authority-staged-pilot-preview.md`, `docs/security/THREAT_MODEL.md`, `docs/privacy/PRIVACY_MODEL.md`, `docs/accessibility/P14_PILOT_PRIVACY_CHECKLIST.md`, `docs/pilot/PILOT_PLAN.md` and `docs/DECISIONS.md`.
- Local-only consent state/count and rollback-safe withdrawal: `src/DCx.Core/Release/PilotMetricAggregateStore.cs` and `tests/DCx.Core.Tests/Release/PilotMetricAggregateStoreTests.cs`.
- Closed incident policy and zero-authority report: `src/DCx.Core/Release/LocalPilotAdministrationPolicy.cs` and `tests/DCx.Core.Tests/Release/LocalPilotAdministrationPolicyTests.cs`.
- Accessible local privacy controls, stable statuses and focus: `src/DCx.Desktop/ReadinessWindow.xaml` and `src/DCx.Desktop/ReadinessWindow.xaml.cs`.
- Native approval/enable/stop/purge/focus/access-key/activation-absence gate and retained machine-readable UIA transcript: `scripts/validate-p14.ps1`.
- Decision and human-AT boundary: `docs/architecture/adr/0087-local-only-pilot-metric-consent-controls.md`, `docs/architecture/adr/0100-local-pilot-administration-preview.md` and `docs/accessibility/P14_PILOT_PRIVACY_CHECKLIST.md`.
- Shared policy and tests: `src/DCx.Core/Release/ReleaseReadiness.cs`, `tests/DCx.Core.Tests/Release/ReleaseReadinessTests.cs`, `tests/DCx.ReadinessReport/Program.cs`.
- The P12 readiness item treats local QuickBooks conformance and the local URL-to-tagged-flattened-PDF branch as passed, while retaining separately authorized QuickBooks sandbox OAuth/API retrieval, Adobe Reader/PDF-conformance and live/human acceptance; its focused policy tests pass 5/5.
- Native accessible UI: `src/DCx.Desktop/ReadinessWindow.xaml`, `src/DCx.Desktop/ReadinessWindow.xaml.cs`, `src/DCx.Desktop/App.xaml.cs`, `src/DCx.Desktop/MainWindow.xaml`, `src/DCx.Desktop/MainWindow.xaml.cs`.
- Reproducible UIA gate: `scripts/validate-p14.ps1`.
- Windows fixture cleanup was separately stabilized in `1c06d97`; three consecutive no-build runs and the P14 checkpoint CI run each passed 146/146.

## Blockers

- P06 transcription/embedding backend gate, P09 Compose gate, P10 production CCB isolation/deployment gate, P11 exact-plan provisioning/credentialed PayPal acceptance/provider eligibility and verification/closure/requeue-policy/authenticated-transport/distributed-fencing/production-scheduling/external-notification gates, P12 PDF/UA/visual/human acceptance plus authorized QuickBooks retrieval/live-provider acceptance, and P13 signed installer/security/human-AT gates are unresolved.
- Privacy notice, terms, CARE/commission policy, provider distribution approvals and legal/compliance review are not approved.
- The readiness dashboard now has automated evidence for an accessible local administration preview, strict aggregate display, a blocked four-stage cohort preview, consent enable/withdrawal and a deterministic incident stop dry-run. It is not a deployed or multi-user pilot administration service or approved enrollment flow. The notice and interaction still require human JAWS/NVDA/Narrator and legal/privacy approval. No approved cohort size/schedule, owner cohort authorization, enrollment system, product event source, network metric transport, final retention policy, support staffing, external notification workflow or human tabletop exercise exists.
- A staged pilot requires explicit owner approval after blocker closure. Production activation remains off by construction in this report.
