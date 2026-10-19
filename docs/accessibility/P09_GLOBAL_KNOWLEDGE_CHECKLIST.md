# P09 global knowledge accessibility checklist

Automated native UIA validation passed on 2026-10-19. It verified heading-first focus, distinct refresh/synchronize/cancel/search access keys, cancellation availability during work, explicit signed synchronization, readable CKB/CCB result names and metadata, offline/private-isolated search status, and the unconfigured default denial path. It also found and drove correction of one list-item automation-name defect. This is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Confirm unconfigured does not sound like an empty catalog |
| JAWS | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| JAWS | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| JAWS | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |
| NVDA | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Human test required |
| NVDA | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| NVDA | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| NVDA | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |
| Narrator | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Human test required |
| Narrator | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| Narrator | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| Narrator | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |

Repeat keyboard-only at 200% scaling and in Windows High Contrast. Confirm that the live region announces stable outcomes once, no private resource appears, no search starts while unconfigured, no sync starts on window open/refresh/search, focus remains usable after cancellation/failure, and result details do not activate a source URI or artifact. Do not configure a real server or use real shared content during this checklist without separate approval.
