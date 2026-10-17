# ADR 0012: Read-only shared readiness policy

- Status: Accepted for local controls
- Date: 2026-10-17

## Context

Release blockers must be understandable through the accessible Windows UI as well as reproducible automation. Duplicating the current blocker list in a report utility and UI would permit drift. A visibility feature must not become an accidental pilot or production activation path.

## Decision

Keep the current triaged blocker set in one immutable core policy. The atomic JSON reporter and native WPF readiness dashboard evaluate that same policy. The dashboard is keyboard- and UIA-accessible, reads only local readiness and aggregate-consent state, and offers refresh only.

The dashboard contains no production activation, pilot enrollment, metric consent mutation, event collection, network transport, publishing or payment action. A `GO` decision still requires every critical/major blocker to be validated and a separate explicit production-activation approval.

## Consequences

- The native view and generated report cannot silently diverge in their current blockers.
- Users and testers can inspect `NO_GO`, assigned next actions, evidence references and privacy-reviewed metric definitions without enabling any external effect.
- UI Automation validates the decision, counts, consent state, lists, refresh control and absence of an activation control.
- This is not a pilot administration console. Enrollment, consent/withdrawal UX, provider approval, support operations and production activation remain blocked and separately governed.
