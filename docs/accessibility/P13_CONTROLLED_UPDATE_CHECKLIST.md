# P13 controlled-update accessibility checklist

Automated Windows UIA evidence: PASS at implementation `c506bb7`, in `artifacts/test-results/p13-a43cd4b336f14798b43d2c1cbe768af8/controlled-update-ui/validation.json`.

| Scenario | Automated UIA | Human JAWS/NVDA/Narrator |
|---|---|---|
| Window opens with keyboard focus on the controlled-updates heading | Pass | Not run |
| Security boundary distinguishes the local fixture from the normal unavailable source | Pass | Not run |
| Check, approval, stage and close expose four distinct access keys | Pass | Not run |
| Approval begins unchecked and staging is disabled before review | Pass | Not run |
| Exact current/candidate versions, UTC validity, addresses, identity, publisher, length and three SHA-256 values are readable | Pass | Not run |
| Check requests no package and announces that nothing was staged or installed | Pass | Not run |
| Unapproved staging is denied before storage and focus moves to approval | Pass | Not run |
| Approved inert staging announces every zero-authority counter and moves focus to updated details | Pass | Not run |
| Approval resets and stage replay remains disabled until a fresh check | Pass | Not run |
| Normal product reports `not_configured`, makes zero requests and infers no update | Pass | Not run |

Human acceptance must repeat the workflow with JAWS, NVDA and Narrator; keyboard only; 200% scaling; High Contrast; long localized publisher/identity/address text; expired, changed and malformed manifests; package-integrity/trust failure; cancellation/window close during review and staging; and repeated checks. Confirm concise reading order, no unexpected virtual-cursor or focus movement, complete digest/value access, announcement of denial/success, and no installer, elevation, activation or restart UI. These human rows remain release-blocking and unverified.
