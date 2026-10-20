# P13 post-recovery return accessibility checklist

Automated native Windows UIA evidence: PASS at `2b8df20`. Human assistive-technology acceptance: NOT RUN and release-blocking.

| Check | Automated UIA | Human JAWS/NVDA/Narrator |
|---|---|---|
| `Return to normal DCx` exposes a stable button name and help text | Pass | Not run |
| `Verify selected backup` exposes a stable name/help text and UIA access key `V` (`Alt+V`) | Pass | Not run |
| Verify can run while normal DCx owns the lease and returns focus without changing data | Pass | Not run |
| Return begins disabled and is not keyboard-focusable before a successful operation | Pass | Not run |
| Failed lease-held backup creates no archive or return authority | Pass | Not run |
| Successful backup enables and focuses Return | Pass | Not run |
| Restore remains separately approval-gated | Pass | Not run |
| Successful restore enables and focuses Return | Pass | Not run |
| WPF mnemonic `Alt+T` is exposed as UIA access key `T` | Pass | Not run |
| Activation starts exactly the current executable with no arguments, shell, or elevation | Pass | Not run |
| Recovery closes; normal DCx reaches offline-ready state and owns keyboard focus | Pass | Not run |
| PackageManager, installer, and production-action counters remain zero | Pass | Not run |
| 200% scaling, High Contrast, keyboard-only order, speech timing, and conflict behavior | Not automated | Not run |

Do not infer a screen-reader pass from UIA. Human testing must confirm announcement timing for the assertive status, the newly enabled Return control, Recovery closure, and focus arrival in normal DCx.
