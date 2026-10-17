# P10 contribution accessibility checklist

Automated native UIA validation passed on 2026-10-17. It verified that global preparation, per-item approval, rights, license, sensitive-data and the separate simulation-send approval all start unchecked; preparation and send start disabled; the no-network-by-default status is exposed; the separately approved exact item reaches only loopback quarantine; and the password control and send approval reset. It is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| JAWS | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| JAWS | Read the loopback-only notice; prepare, separately approve, cancel/send, and hear quarantine or indeterminate-cancel status | UNVERIFIED | Use generated fixture content and credential only |
| NVDA | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| NVDA | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| NVDA | Read the loopback-only notice; prepare, separately approve, cancel/send, and hear quarantine or indeterminate-cancel status | UNVERIFIED | Use generated fixture content and credential only |
| Narrator | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| Narrator | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| Narrator | Read the loopback-only notice; prepare, separately approve, cancel/send, and hear quarantine or indeterminate-cancel status | UNVERIFIED | Use generated fixture content and credential only |

Repeat with 200% scaling, High Contrast, keyboard-only operation, every scanner finding class, an expired item approval, invalid/non-loopback origins, authentication denial, cancellation and server unavailability. Confirm that item/endpoint/token changes reset send approval, the token clears after every attempt, and no status announces the token, detected secret or personal value. No real content may be submitted during this checklist.
