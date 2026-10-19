# Accessibility requirements

Accessibility failures are release blockers, not enhancement requests.

## Interaction contract

- Every public control is reachable and operable by keyboard with a visible focus indicator.
- Controls expose stable UIA names, control types, states, descriptions, and relationships without duplicate verbosity.
- Dynamic status, progress, errors, approvals, takeover, and handback are available as concise accessible text and appropriate live announcements.
- Agent actions never move keyboard/screen-reader focus unless the user asked for that navigation or an announced accessible confirmation requires it.
- User input immediately pauses conflicting agent input; Escape is the consistent safe cancel/back-out key where platform conventions permit.
- Web content remains a live WebView2 accessibility tree. Semantic repairs are opt-in overlays that are reversible and do not replace the original DOM.
- Color is never the sole state indicator. Text supports Windows scaling, high contrast, and reflow without truncating actions.

## Test layers

1. Unit tests for names, policies, focus state machines, announcements, and keyboard bindings.
2. Native Windows UIA automation for role/name/state/focus/tab order and takeover flows.
3. Accessibility Insights/automated scanners when available.
4. Human testing with current installed JAWS, NVDA, and Narrator, recorded separately by version and scenario.

Automated P00 UIA smoke evidence does not satisfy the human screen-reader gate.

## P13 controlled updates

The native controlled-update window begins at a focusable heading and exposes Check, a separately default-unchecked approval, Stage and Close through distinct access keys. Review details are read-only and include exact versions, UTC validity, manifest/package addresses, package identity, publisher, length and three labeled SHA-256 values. Check requests no package. Denied Stage moves focus to approval and creates no storage. Successful fixture staging moves focus to updated details, resets approval, disables replay until a fresh check and announces network, execution, installer, elevation, activation, restart and production effects. Normal product composition reports `not_configured` and infers no update. Automated and outstanding human rows are recorded in `P13_CONTROLLED_UPDATE_CHECKLIST.md`.

## P11 failed-payout hold review

The generated-data CARE operations fixture exposes a privacy-minimized failed-payout hold through stable UIA names and distinct access keys. Approval begins unchecked. Denial returns focus to approval; an accepted non-financial intent returns focus to updated details, disables replay and announces provider calls, financial mutations, payout attempts and notification transports as zero. The read-only contributor period list adds fixed local status notices, and a separate labeled list exposes subject-bound failed-payout notice records. Each notice says that it is local, no external message was sent and no automatic retry occurs. Automated details and the outstanding human JAWS/NVDA/Narrator rows are recorded in `P11_FAILED_PAYOUT_HOLD_CHECKLIST.md`.
