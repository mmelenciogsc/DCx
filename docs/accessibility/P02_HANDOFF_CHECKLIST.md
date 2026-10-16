# P02 human control-handoff checklist

Status: **UNVERIFIED — automated UIA validation does not replace a human assistive-technology session.**

- [ ] UNVERIFIED — JAWS announces the scoped control request, approved origin, current owner, manual-input instruction, handback, completion and cancellation exactly once.
- [ ] UNVERIFIED — NVDA announces the same state changes without moving browse-mode focus unexpectedly.
- [ ] UNVERIFIED — Narrator can approve/reject, take control, edit the live page field, hand back and cancel using only the keyboard.
- [ ] UNVERIFIED — Physical keyboard or pointer input during agent control immediately returns control and does not discard the user's input.
- [ ] UNVERIFIED — Focus lands in the requested manual field and returns to that field after the approved agent continuation.
- [ ] UNVERIFIED — Action log order and provenance are understandable in speech/braille and contain no field value.
- [ ] UNVERIFIED — Cross-origin or stale-page denial is announced without exposing internal stack traces.
- [ ] UNVERIFIED — High contrast, 200% scaling and visible focus clearly distinguish User, Approval pending, Agent, Paused and Cancelled modes.

