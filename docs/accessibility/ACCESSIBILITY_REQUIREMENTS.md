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

