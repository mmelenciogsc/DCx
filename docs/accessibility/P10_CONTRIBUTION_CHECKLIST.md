# P10 contribution accessibility checklist

Automated native UIA validation passed on 2026-10-17. It verified that global preparation, per-item approval, rights, license, sensitive-data, send, status and withdrawal approvals all start unchecked; actions start disabled; and the no-network-by-default status is exposed. The separately approved exact CKB item reached loopback quarantine, cross-origin reconciliation was denied, body-free status announced `Approved`, a separate withdrawal created the deprecation, and the password/action approvals reset after each attempt. A second keyboard/UIA flow selected CCB, loaded a schema-v2 package, exposed complete admitted review text and bound manifest identity/version, cleared and did not announce the full path, submitted exact bytes into quarantine, cleared the review after acceptance, locked candidate identity and announced minimized `Quarantined` status. It is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| JAWS | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| JAWS | Select CCB, browse/load a package, review all admitted text, hear path-clearing/digest status, and confirm identity locks after intake | UNVERIFIED | Use generated fixture package only |
| JAWS | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |
| NVDA | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| NVDA | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| NVDA | Select CCB, browse/load a package, review all admitted text, hear path-clearing/digest status, and confirm identity locks after intake | UNVERIFIED | Use generated fixture package only |
| NVDA | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |
| Narrator | Read the local-only explanation and initial unchecked/disabled states | UNVERIFIED | Human test required |
| Narrator | Review findings, attest, approve exact item, change content and hear approval clear | UNVERIFIED | Human test required |
| Narrator | Select CCB, browse/load a package, review all admitted text, hear path-clearing/digest status, and confirm identity locks after intake | UNVERIFIED | Use generated fixture package only |
| Narrator | Read the loopback-only notice; prepare, separately approve send/status/withdrawal, cancel, and hear quarantine/approved/withdrawn or indeterminate status | UNVERIFIED | Use generated fixture content and credential only |

Repeat with 200% scaling, High Contrast, keyboard-only operation, every scanner finding class, an expired item approval, malformed/oversize/reparse CCB packages, invalid/non-loopback/cross-origin requests, authentication denial, cancellation and server unavailability. Confirm that controls lock in flight; candidate identity remains locked during uncertain/accepted nonterminal states; item/endpoint/token changes reset approvals; uncertain intake disables blind retry; withdrawal requires a fresh exact `Approved` status; the token clears after every attempt; and no status announces the token, full package path, body, detected secret or personal value. No real content may be submitted during this checklist.
