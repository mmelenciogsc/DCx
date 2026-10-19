# P11 failed-payout hold accessibility checklist

Automated evidence source: `a49ff3c`. Human assistive-technology rows remain release-blocking until performed by a person.

| Check | Automated result | Human result |
|---|---|---|
| Generated-data boundary and zero-authority help text are exposed before action | Pass | Not run |
| Load, three closed decisions, approval and record controls have stable UIA names and distinct access keys | Pass | Not run |
| Review contains the opaque reference, CARE month, USD amount, closed codes, UTC time and digest | Pass | Not run |
| Review excludes email, destination, provider/account data and free-form input | Pass | Not run |
| Approval begins unchecked and resets after load, selection, denial and success | Pass | Not run |
| Recording without approval is denied and moves focus to approval | Pass | Not run |
| Success moves focus to updated details and announces all four zero-authority counters | Pass | Not run |
| A recorded decision disables replay and leaves the payout held | Pass | Not run |
| Five fixed contributor-facing local notices are present in the read-only period list | Pass | Not run |
| 200% text scaling and high contrast preserve labels, controls and focus indication | Not automated | Not run |
| JAWS 2025 reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |
| NVDA 2026.1.1 reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |
| Narrator reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |

Automated evidence is `artifacts/test-results/p11-56d8ec92d6504778a626d1aeeccf49da/recipient-snapshot-uia.json`. It proves local generated-data behavior only; it does not prove external notification delivery, authenticated finance operations or human comprehension.
