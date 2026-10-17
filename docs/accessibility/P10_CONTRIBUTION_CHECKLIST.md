# P10 contribution accessibility checklist

Automated native UIA validation passed on 2026-10-17. It verified that global preparation, per-item approval, rights, license and sensitive-data attestations all start unchecked; preparation starts disabled; and the no-upload status is exposed. It is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| JAWS | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| NVDA | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| NVDA | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| Narrator | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| Narrator | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |

Repeat with 200% scaling, High Contrast, keyboard-only operation, every scanner finding class and an expired item approval. Confirm that finding codes never announce the detected secret or personal value. No real content may be submitted during this checklist.
