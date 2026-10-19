# P13 controlled-update accessibility checklist

Automated Windows UIA evidence: PASS at exact validation source `fc23b48`, in `artifacts/test-results/p13-e0abd97bf39849e792dec2f561fa1b37/controlled-update-ui/validation.json`. Close follows the final-review group in XAML reading/tab order. The activation transaction and internal Windows `PackageManager` adapter are deliberately absent from normal user-facing composition and have no UI or command route until a signed lifecycle, one-use accessible authority and separate human-AT gate are approved. The real Windows package API was not invoked.

| Scenario | Automated UIA | Human JAWS/NVDA/Narrator |
|---|---|---|
| Window opens with keyboard focus on the controlled-updates heading | Pass | Not run |
| Security boundary distinguishes the local fixture from the normal unavailable source | Pass | Not run |
| Check, staging approval, stage, final-review request, verify and close expose six distinct access keys | Pass | Not run |
| Approval begins unchecked and staging is disabled before review | Pass | Not run |
| Exact current/candidate versions, UTC validity, addresses, identity, publisher, length and three SHA-256 values are readable | Pass | Not run |
| Check requests no package and announces that nothing was staged or installed | Pass | Not run |
| Unapproved staging is denied before storage and focus moves to approval | Pass | Not run |
| Approved inert staging announces every zero-authority counter and moves focus to updated details | Pass | Not run |
| Published staging contains one exact two-file directory and leaves no loose or incoming item | Pass | Not run |
| Final-integrity request starts unchecked; denial moves focus to it | Pass | Not run |
| Approved final review announces one bound digest and every zero-authority counter | Pass | Not run |
| Final-integrity request resets and cannot be replayed without a fresh staged result | Pass | Not run |
| Approval resets and stage replay remains disabled until a fresh check | Pass | Not run |
| Normal product reports `not_configured`, makes zero requests and infers no update | Pass | Not run |

Human acceptance must repeat the workflow with JAWS, NVDA and Narrator; keyboard only; 200% scaling; High Contrast; long localized publisher/identity/address text; expired, changed and malformed manifests; package-integrity/trust failure; cancellation/window close during review, staging and final-integrity review; and repeated checks. Confirm concise reading order, no unexpected virtual-cursor or focus movement, complete digest/value access, announcement of denial/success, a clear distinction between evidence and authority, and no installer, elevation, activation or restart UI. These human rows remain release-blocking and unverified.
