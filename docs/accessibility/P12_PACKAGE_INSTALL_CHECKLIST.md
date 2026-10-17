# P12 package-install review accessibility checklist

Automated Windows UIA validation passed on 2026-10-17 for the inert package-installation review. It verified the heading and validation disclosure; complete read-only request details; default-unchecked approval; unchecked denial; exact approval; cancellation; disabled reuse; assertive statuses; and zero child installer processes. It is not a human screen-reader result and no package was installed.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the validation/no-execution disclosure and every exact package, publisher, digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| JAWS | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |
| NVDA | Read the validation/no-execution disclosure and every exact package, publisher, digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| NVDA | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |
| Narrator | Read the validation/no-execution disclosure and every exact package, publisher, digest, scope, elevation and expiry field | UNVERIFIED | Human test required |
| Narrator | Reach the unchecked approval, record approval, cancel a separate request and confirm assertive status/focus | UNVERIFIED | Human test required |

Repeat with 200% scaling, High Contrast, keyboard only, Escape cancellation, long localized path/publisher text, expired review, changed package/trust evidence and unavailable verifier. Confirm that approval never defaults on, the complete exact request is available without a pointer, focus remains predictable, stable denial is announced, completed approval cannot be reused, and no installer or elevation UI appears. Real signed install/update/uninstall requires a separate checklist and remains blocked.
