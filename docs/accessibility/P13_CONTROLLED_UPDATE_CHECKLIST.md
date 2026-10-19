# P13 controlled-update accessibility checklist

Automated Windows UIA evidence: PASS at exact validation source `69a069b`, in `artifacts/test-results/p13-f4cb4cdf196f41c0820a394b22335dfe/controlled-update-ui/validation.json`. Close follows staging, final review and synthetic transaction validation in XAML reading/tab order. Eight distinct access keys pass. Stage, final review and transaction validation each receive keyboard focus before invocation; denial then focuses the corresponding unchecked approval. One approved validation reports `ValidationNoOp`, zero PackageManager calls/no registration/restart, unchanged staged-file hashes and all authority counters zero, then resets. The isolated hardened native workflow passed once and then five consecutive repetitions before the final integrated pass. The validation-only executor cannot reference the internal Windows adapter. Normal composition remains `not_configured`, and the real Windows package API was not invoked. A signed lifecycle, real activation workflow and separate human-AT gate remain blocked.

| Scenario | Automated UIA | Human JAWS/NVDA/Narrator |
|---|---|---|
| Window opens with keyboard focus on the controlled-updates heading | Pass | Not run |
| Security boundary distinguishes the local fixture from the normal unavailable source | Pass | Not run |
| Check, staging approval, stage, final-review request, verify, transaction-validation approval, no-install validation and close expose eight distinct access keys | Pass | Not run |
| Approval begins unchecked and staging is disabled before review | Pass | Not run |
| Exact current/candidate versions, UTC validity, addresses, identity, publisher, length and three SHA-256 values are readable | Pass | Not run |
| Check requests no package and announces that nothing was staged or installed | Pass | Not run |
| Unapproved staging is denied before storage and focus moves to approval | Pass | Not run |
| Each denied action originates from confirmed keyboard focus before focus moves to its approval | Pass | Not run |
| Approved inert staging announces every zero-authority counter and moves focus to updated details | Pass | Not run |
| Published staging contains one exact two-file directory and leaves no loose or incoming item | Pass | Not run |
| Final-integrity request starts unchecked; denial moves focus to it | Pass | Not run |
| Approved final review announces one bound digest and every zero-authority counter | Pass | Not run |
| Final-integrity request resets and cannot be replayed without a fresh staged result | Pass | Not run |
| Transaction-validation approval starts unchecked; denial focuses it | Pass | Not run |
| Approved transaction validation reports `ValidationNoOp`, zero PackageManager calls, no registration/restart and unchanged staged hashes | Pass | Not run |
| Transaction-validation approval resets after one attempt | Pass | Not run |
| Approval resets and stage replay remains disabled until a fresh check | Pass | Not run |
| Normal product reports `not_configured`, makes zero requests and infers no update | Pass | Not run |

Human acceptance must repeat the workflow with JAWS, NVDA and Narrator; keyboard only; 200% scaling; High Contrast; long localized publisher/identity/address text; expired, changed and malformed manifests; package-integrity/trust failure; cancellation/window close during review, staging, final-integrity review and transaction validation; and repeated checks. Confirm concise reading order, no unexpected virtual-cursor or focus movement, complete digest/value access, announcement of denial/success, a clear distinction between synthetic evidence and real authority, and no installer, elevation, activation or restart UI. These human rows remain release-blocking and unverified.
