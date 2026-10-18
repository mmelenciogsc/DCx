# P12 assisted Facebook post-search accessibility checklist

Automated Windows UIA validation passed on 2026-10-18 against a disclosed literal-loopback fixture. It proved manual-login handoff, explicit user confirmation, search navigation, current-post inspection, result focus, nine distinct access keys, copy availability, and clearing of the result when a blocked page replaces the post. The fixture is not Facebook, accepts no real credential and is not a human screen-reader result.

| Assistive technology | Check | Status | Notes |
|---|---|---|---|
| JAWS | Complete Facebook login/MFA/consent manually, confirm handback, open one candidate and inspect the current post | UNVERIFIED | Live human test and provider approval required |
| JAWS | Read result summary, date, visible text, media descriptions and direct address; confirm blocked/checkpoint pages clear prior output | UNVERIFIED | Human test required |
| NVDA | Complete Facebook login/MFA/consent manually, confirm handback, open one candidate and inspect the current post | UNVERIFIED | Live human test and provider approval required |
| NVDA | Read result summary, date, visible text, media descriptions and direct address; confirm blocked/checkpoint pages clear prior output | UNVERIFIED | Human test required |
| Narrator | Complete Facebook login/MFA/consent manually, confirm handback, open one candidate and inspect the current post | UNVERIFIED | Live human test and provider approval required |
| Narrator | Read result summary, date, visible text, media descriptions and direct address; confirm blocked/checkpoint pages clear prior output | UNVERIFIED | Human test required |

Repeat with keyboard only, 200% scaling, High Contrast, browser zoom, long post text, image-only/video-only posts, no date, no media label, login expiry, consent dialog, checkpoint/challenge, navigation during inspection and a non-post Facebook address. Confirm that focus remains in the shared DCx viewport for manual work, returns predictably to the finder, stale output is cleared, no hidden/canonical address is accepted, and no cookie/password/storage value appears.
