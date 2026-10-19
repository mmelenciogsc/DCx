# P13 accessible package-removal review checklist

Automated native Windows UIA evidence: PASS at `211c204`. Human assistive-technology acceptance: NOT RUN and release-blocking.

| Check | Automated result | Human result |
|---|---|---|
| Opening Uninstall only opens review and starts no removal | Pass | Not run |
| Initial keyboard focus reaches `Uninstall DCx heading` | Pass | Not run |
| Exact identity, operation, scope, consequences, expiry, and one SHA-256 digest are readable | Pass | Not run |
| Consequences and backup acknowledgments begin unchecked | Pass | Not run |
| Distinct access keys `Alt+C`, `Alt+B`, `Alt+R`, `Alt+V`, and `Alt+L` are exposed | Pass | Not run |
| Keyboard-origin denial moves focus to the missing acknowledgment | Pass | Not run |
| Assertive status announces denial and terminal validation result | Pass | Not run |
| Successful validation resets acknowledgments and prevents replay | Pass | Not run |
| Normal unpackaged product announces unavailable and disables approval/action controls | Pass | Not run |
| `Alt+R` starts exactly the current DCx executable with only `--recovery` and no shell/elevation | Pass | Not run |
| Separate recovery window receives initial backup-path focus while main DCx remains open | Pass | Not run |
| Recovery handoff explicitly reports backup not created, no app close, and no uninstall | Pass | Not run |
| Fixture launch without its exact environment gate is denied | Pass | Not run |
| Uninstall validation fixture invokes no PackageManager, child, close, elevation, uninstall, or production action | Pass | Not run |
| JAWS reading order, forms mode, mnemonics, and live announcements | Not sufficient—UIA only | Not run |
| NVDA reading order, forms mode, mnemonics, and live announcements | Not sufficient—UIA only | Not run |
| Narrator scan/navigation order, mnemonics, and live announcements | Not sufficient—UIA only | Not run |
| High contrast, 200% scaling, keyboard-only review, and signed clean-VM uninstall lifecycle | Not sufficient—local fixture only | Not run |

Do not convert an automated UIA pass into a human screen-reader claim. Production acceptance additionally requires the approved signed package identity, explicit owner authorization, real close/result-handoff behavior, backup/recovery verification, rollback, and clean-VM lifecycle evidence.
