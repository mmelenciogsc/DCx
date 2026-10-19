# P09 global knowledge accessibility checklist

Automated native UIA validation passed on 2026-10-19. It verified heading-first focus; distinct refresh/synchronize/cancel/search access keys; cancellation availability during work; explicit signed synchronization; readable CKB/CCB result names and metadata; offline/private-isolated search status; and the unconfigured default denial path. It also verified seven distinct enrollment access keys, initially disabled/unchecked approval, exact fingerprint/address review, candidate-change invalidation, save/remove interlocks, zero enrollment sockets, production boundary text and focus return. The isolated test store cannot read or overwrite a real user enrollment. UIA previously found and drove correction of one list-item automation-name defect. This is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Confirm unconfigured does not sound like an empty catalog |
| JAWS | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| JAWS | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| JAWS | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |
| JAWS | Review the exact trust-root fingerprint/address, approve, replace and clear enrollment | UNVERIFIED | Use non-production fixture material; confirm approval resets and focus returns |
| NVDA | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Human test required |
| NVDA | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| NVDA | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| NVDA | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |
| NVDA | Review the exact trust-root fingerprint/address, approve, replace and clear enrollment | UNVERIFIED | Use non-production fixture material; confirm approval resets and focus returns |
| Narrator | Open Global knowledge from the main shell and read the boundary/status | UNVERIFIED | Human test required |
| Narrator | Review active release, digest, resource count and trust-policy details | UNVERIFIED | Use synthetic local fixture only |
| Narrator | Invoke sync, move to Cancel while busy, then hear atomic completion/cancellation | UNVERIFIED | No production endpoint |
| Narrator | Search offline and review CKB/CCB list items and details | UNVERIFIED | Confirm human-readable item names |
| Narrator | Review the exact trust-root fingerprint/address, approve, replace and clear enrollment | UNVERIFIED | Use non-production fixture material; confirm approval resets and focus returns |

Repeat keyboard-only at 200% scaling and in Windows High Contrast. Confirm that the live region announces stable outcomes once; no private resource appears; no search starts while unconfigured; no sync starts on window open/refresh/search/enrollment review/save; the full local key path is absent after review; stale approval never survives a change; focus remains usable after close/cancellation/failure; and result details do not activate a source URI or artifact. Verify a production fingerprint through an independent trusted channel. Do not configure a real server or use real shared content during this checklist without separate approval.
