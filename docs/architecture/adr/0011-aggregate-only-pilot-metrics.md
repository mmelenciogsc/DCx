# ADR 0011: Aggregate-only pilot metrics

- Status: Accepted for local controls
- Date: 2026-10-17

## Context

Pilot decisions need measured completion, latency, cost, reuse, reliability, accessibility, synchronization and CARE variance. Raw event streams, free-form dimensions and identifiers would create avoidable privacy and reidentification risk. Implementing storage must not itself activate a pilot.

## Decision

The local pilot metric store defaults off. It accepts only the reviewed metric catalog, bounded numeric samples and metric-specific enum dimensions. It immediately aggregates by UTC day into count, sum, minimum and maximum; there is no raw-event table or free-form dimension field.

Consent records contain only enabled state, notice version and UTC recording time. Notice versions cannot move backward. Disabling consent and deleting all local aggregates occur in one SQLite transaction. No product event source or network transport is connected until later approval.

## Consequences

- Prompt, response, URL, path, keystroke, account and user identifiers have no storage field in this subsystem.
- Ratio metrics can be reconstructed from bounded zero/one samples; token routes and reviewed failure classes retain limited aggregate diagnostic value.
- Opt-out has an immediate local purge behavior.
- ADR 0087 adds automated native evidence for local-only consent and withdrawal/purge. Human assistive-technology acceptance, an approved production privacy notice, retention policy, pilot enrollment, administration and explicit activation remain separate blockers.
