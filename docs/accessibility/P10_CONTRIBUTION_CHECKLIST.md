# P10 contribution accessibility checklist

Automated native UIA validation passed on 2026-10-17. It verified that global preparation, per-item approval, rights, license, sensitive-data, send, status and withdrawal approvals all start unchecked; actions start disabled; and the no-network-by-default status is exposed. The separately approved exact item reached loopback quarantine, cross-origin reconciliation was denied, body-free status announced `Approved`, a separate withdrawal created the deprecation, and the password/action approvals reset after each attempt. It is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| JAWS | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| JAWS | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |
| NVDA | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| NVDA | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| NVDA | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |
| Narrator | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| Narrator | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| Narrator | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |

Repeat with 200% scaling, High Contrast, keyboard-only operation, every scanner finding class, an expired item approval, invalid/non-loopback/cross-origin requests, authentication denial, cancellation and server unavailability. Confirm that controls lock in flight; item/endpoint/token changes reset approvals; uncertain intake disables blind retry; withdrawal requires a fresh exact `Approved` status; the token clears after every attempt; and no status announces the token, body, detected secret or personal value. No real content may be submitted during this checklist.
