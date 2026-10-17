# Opt-in pilot plan

Status: draft only. The current automated decision is `NO_GO`; no pilot enrollment or telemetry collection is authorized.

## Entry conditions

- Signed package install/update/uninstall and rollback pass on clean Windows 11 VMs.
- JAWS, NVDA and Narrator human acceptance has no unresolved major issue.
- OpenAI, PayPal, Facebook/QuickBooks and other provider use is documented, eligible and separately authorized where applicable.
- Privacy notice, terms, CARE/commission policy, consent record and support/incident ownership are approved.
- Every critical/major readiness issue is validated, the automated report changes to `GO`, and the owner explicitly approves activation.

## Proposed stages

1. Internal fixture-only cohort with local models/mocks and no contribution upload or payment.
2. Small opt-in accessibility cohort with production side effects still disabled.
3. Provider sandbox cohort only after each provider acceptance gate passes.
4. Separately approved production pilot with reversible feature flags and payout/live contribution controls still independently gated.

Each participant receives an accessible scope statement, data disclosure, stop/withdraw control and support route. Enrollment is unchecked by default. Leaving the pilot stops future measurement and follows the approved retention/deletion policy.

## Metrics

The implementation defines 12 aggregate metrics: completion rate, duration, input/output token totals, tested CCB reuse ratio, retrieval success, crashes, user-control revocations, false agent claims, accessibility failures, global-sync bytes and CARE reconciliation variance. Metrics contain no prompt, output body, URL, local path, keystroke, field value or provider account identifier.

## Stop conditions

Immediately stop the affected stage for credential exposure, unauthorized external action, payout/reconciliation variance, private-data transfer, unrecoverable loss, focus/control safety failure, false high-impact agent claim, inaccessible critical flow, signing/update failure or a severity-one incident. Preserve minimized evidence, disable the relevant feature flag, follow the incident runbook and require a new readiness report before resumption.
