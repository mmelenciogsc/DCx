# P12 package-install review accessibility checklist

Automated Windows UIA validation was rerun on 2026-10-19 from `fc23b48`; evidence is `artifacts/test-results/p12-package-approval-d582b16ecdf9444183027b7dc0271c5b/`. It verified 12/12 policy cases, the heading and validation disclosure; complete read-only request details; default-unchecked approval; unchecked denial; exact approval; cancellation; disabled reuse; assertive statuses; and zero child installer processes. It is not a human screen-reader result, no package was installed, and P13's internal adapter is not reachable from this review.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the validation/no-execution disclosure and every exact package, publisher, signer-certificate digest, package/proposal digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| JAWS | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |
| NVDA | Read the validation/no-execution disclosure and every exact package, publisher, signer-certificate digest, package/proposal digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| NVDA | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |
| Narrator | Read the validation/no-execution disclosure and every exact package, publisher, signer-certificate digest, package/proposal digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| Narrator | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |

Repeat with 200% scaling, High Contrast, keyboard only, Escape cancellation, long localized path/publisher text, expired review, changed package/trust evidence and unavailable verifier. Confirm that approval never defaults on, the complete exact request is available without a pointer, focus remains predictable, stable denial is announced, completed approval cannot be reused, and no installer or elevation UI appears. Real signed install/update/uninstall requires a separate checklist and remains blocked.
