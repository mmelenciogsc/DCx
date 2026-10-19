# ADR 0102: Preview staged pilot cohorts without creating enrollment authority

- Status: Accepted for blocked local readiness review; pilot enrollment and activation remain unavailable
- Date: 2026-10-19

## Context

P14 requires a staged opt-in pilot. The local administration preview proved aggregate review and incident-stop behavior but exposed no concrete cohort sequence. A free-form draft can drift from the executable readiness policy, while implementing enrollment before blocker closure, legal approval and owner authorization would create unsafe authority.

## Decision

Define four ordered future stages in product-owned immutable policy: owner-supervised local rehearsal, assistive-technology cohort, limited opt-in cohort and expanded release-candidate cohort. Every stage states its audience, entry gate and exit gate. Each entry requires approval of the previous exit plus the stage-specific accessibility, privacy, support, incident, provider or release evidence. Production activation remains a separate explicit decision after the final stage.

Expose this only through the existing local administration preview while the shared readiness result is a fully triaged blocked `NO_GO`. Report `blocked-not-enrolled`, four future stages, zero enrollment controls, zero product event sources, zero network metric transports, zero activation controls, no recorded owner approval, and no approved cohort size or schedule. A nonblocked or production-approved state cannot be interpreted by this preview and fails closed.

The native window renders the four descriptions as read-only list items. It adds no participant record, roster, identifier, consent record, date, count, mutation action, service call or enrollment route. Existing validator assertions continue to reject controls named or equivalent to **Enroll pilot**, **Send metrics** or **Activate production**.

## Consequences

- The proposed sequence and gates are executable, ordered and accessibility-tree visible instead of being prose only.
- The preview cannot start even the first stage. Cohort size, duration, schedule, retention, support staffing and exact participants require later approved policy and explicit owner authority.
- The stage catalog stores no participant or telemetry data and performs no network or external action.
- Native automation is not human JAWS/NVDA/Narrator acceptance. A staged pilot remains blocked by all seven current critical/major issues and the separate legal, provider, security, operational and human gates.
