# P11 failed-payout hold accessibility checklist

Generated UI evidence source: `a49ff3c`. Schema-v15 durability is implemented at `1e3596a`; default-off authenticated durable transport/native composition is implemented at `a297811` and hardened at `a23207d`. Human assistive-technology rows remain release-blocking until performed by a person.

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
| Authenticated durable listing, denial and success preserve heading/detail focus and the same UIA contract | Pass | Not run |
| The authenticated boundary is disclosed as synthetic local validation rather than production authentication | Pass | Not run |
| The ephemeral validation signing key is deleted before evidence is retained | Pass | Not run |
| Five fixed contributor-facing local notices are present in the read-only period list | Pass | Not run |
| 200% text scaling and high contrast preserve labels, controls and focus indication | Not automated | Not run |
| JAWS 2025 reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |
| NVDA 2026.1.1 reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |
| Narrator reads review, approval, denial, success and local notices without duplicate verbosity | Not automated | Not run |

Current automated evidence is `artifacts/test-results/p11-9cb78d51e3e747a0a08d5758544bd0b8/recipient-snapshot-uia.json` for the generated surface and `operations-loopback-uia.json` for the real child-Kestrel/SQLite composition. It proves authenticated local synthetic-ledger behavior with zero provider/financial/payout/notification authority; it does not prove production authentication, external notification delivery or human comprehension.
