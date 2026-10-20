# P13 backup verification accessibility checklist

Automated native UIA evidence: PASS at `7ff6387`. Human assistive-technology acceptance: NOT RUN and release-blocking.

| Check | Automated UIA | Human JAWS/NVDA/Narrator |
|---|---|---|
| Verify selected backup exposes a stable name and help text | Pass | Not run |
| WPF mnemonic is exposed as UIA access key `V` (`Alt+V`) | Pass | Not run |
| Verification is enabled while normal DCx remains open | Pass | Not run |
| Success announces file count/bytes and says no data changed | Pass | Not run |
| Archive and source data remain byte/metadata unchanged | Pass | Not applicable |
| Verification does not enable Return or create recovery authority | Pass | Not run |
| Duplicate/unknown manifest fields and duplicate payload paths fail closed | Pass | Not applicable |
| Keyboard order, focus return, 200% scaling, High Contrast and announcement timing | Not automated | Not run |

Do not infer a screen-reader pass from UIA. Human testing must confirm the status announcement is concise, that focus returns to Verify, and that users can distinguish verification from restore.
