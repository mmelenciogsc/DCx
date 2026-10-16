# P01 human screen-reader checklist

Status: **UNVERIFIED — no human screen-reader session has been performed for this build.**

Automated UIA validation is recorded in `docs/progress/PHASE-01.md`; it does not satisfy any unchecked row below.

For each reader, record version, Windows build, tester, date, speech/braille configuration, result, and issue link.

## JAWS

- [ ] UNVERIFIED — Window title, toolbar, profile selector, tab list, live page and status are announced concisely.
- [ ] UNVERIFIED — `Ctrl+L`, `Ctrl+T`, `Ctrl+W`, `Ctrl+Tab`, `Alt+Left`, `Alt+Right`, `F6`, and `Escape` do not conflict unexpectedly.
- [ ] UNVERIFIED — WebView browse/forms modes permit direct navigation and editing of a labeled login-style field.
- [ ] UNVERIFIED — History, bookmarks and downloads lists expose count, current item and activation.
- [ ] UNVERIFIED — Agent drawer unavailable state and close action are understandable without vision.
- [ ] UNVERIFIED — Navigation/loading/failure announcements do not steal the virtual cursor or repeat excessively.

## NVDA

- [ ] UNVERIFIED — Window title, toolbar, profile selector, tab list, live page and status are announced concisely.
- [ ] UNVERIFIED — Browser shortcuts and NVDA browse/focus modes coexist without unexpected interception.
- [ ] UNVERIFIED — A labeled login-style field can be reached, edited and exited using only the keyboard.
- [ ] UNVERIFIED — History, bookmarks, downloads and Agent drawer are fully operable.
- [ ] UNVERIFIED — Loading and error status is timely and non-repetitive.

## Narrator

- [ ] UNVERIFIED — Scan mode and tab navigation reach every public control in a logical order.
- [ ] UNVERIFIED — A labeled login-style field can be reached and edited in the live WebView.
- [ ] UNVERIFIED — Profile and tab names distinguish Default, Work and Private contexts.
- [ ] UNVERIFIED — History, bookmarks, downloads and Agent drawer are fully operable.
- [ ] UNVERIFIED — Focus remains stable across navigation and panel open/close.

## Visual and keyboard-only companion checks

- [ ] UNVERIFIED — 200% Windows scaling and text scaling do not clip required controls.
- [ ] UNVERIFIED — Windows high contrast preserves visible focus and state.
- [ ] UNVERIFIED — Keyboard-only user can complete every P01 action without a pointer.

