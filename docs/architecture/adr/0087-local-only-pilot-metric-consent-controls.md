# ADR 0087: Add local-only pilot metric consent controls

- Status: Accepted for local privacy controls; pilot enrollment and production collection remain blocked
- Date: 2026-10-19

## Context

ADR 0011 created a default-off aggregate-only metric store with atomic opt-out purge, while ADR 0012 deliberately limited the first readiness dashboard to read-only inspection. P14 requires a staged opt-in pilot, but users had no native keyboard-accessible way to grant or withdraw the store's local consent. Reporting that consent was disabled was not equivalent to providing user control.

The local setting must not become authority to enroll a user, connect product events, transmit metrics, change the `NO_GO` decision or activate production. Withdrawal must remain possible even if an older client encounters consent written under a newer notice version.

## Decision

The readiness window retains its read-only release decision and evidence. A separate local metric privacy group displays the complete notice-version-1 minimization boundary and provides two independent, one-use approvals:

1. an initially unchecked consent followed by **Enable local aggregate recording**; and
2. an initially unchecked destructive acknowledgment followed by **Stop recording and purge local aggregates**.

Both approvals reset after use. Buttons remain disabled until the applicable approval is checked. Operations disable each other and refresh to prevent overlapping state changes. Completion or stable failure text is exposed through an assertive, keyboard-focusable UIA status. The current consent state includes the number of retained daily aggregate buckets.

Enabling changes only the existing local consent row. It connects no product event source or network transport and creates no pilot enrollment or production authority. Stopping atomically disables recording and deletes all metric aggregates. A lower notice version remains forbidden for enabling, but opt-out from an older client preserves the highest stored notice version while purging; withdrawal cannot be blocked by notice-version rollback protection.

The dashboard continues to expose no production-activation control. The release decision remains `NO_GO` while any current critical or major blocker remains.

## Consequences

- Users can grant and revoke the already-defined local storage consent without implying participation in a pilot.
- Native UIA proves default-off state, separate approvals, focused results, six distinct privacy/administration/refresh access keys, zero retained buckets after purge and continued absence of production activation. ADR 0100 adds the local administration preview without expanding consent authority.
- Local consent is not consent to future event sources, metric transport, support access, export, provider use or policy changes. Each requires a separately reviewed boundary and, where applicable, new user approval.
- The SQLite file remains local per-user state. Same-user tampering and final retention/legal policy remain release concerns, and human JAWS/NVDA/Narrator testing remains blocked.

## Alternatives rejected

- Treat a read-only consent label as sufficient user control: it offers no withdrawal path.
- One checkbox that immediately mutates state: it makes accidental activation or purge easier and provides no separate destructive acknowledgment.
- Permit an older client to lower the notice version during opt-out: withdrawal should purge without rewriting consent history backward.
- Add an event producer or network sender with the controls: that would exceed the approved local-only scope and begin an unauthorized pilot.
