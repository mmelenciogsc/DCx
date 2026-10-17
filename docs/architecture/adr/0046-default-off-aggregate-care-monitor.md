# ADR 0046: Monitor aggregate CARE transitions without automatic processing

- Status: Accepted for default-off local simulation; production scheduling and notification remain blocked
- Date: 2026-10-18

## Context

The CARE operations health contract can identify scheduled retries, due work and blocked records without revealing provider or subject identifiers. Before this decision, an operator had to request that health manually. Automatically processing due work would bypass the exact-review and explicit-approval boundary, while logging raw state, exceptions or record identifiers would weaken privacy. A local fixture also cannot establish production notification delivery, acknowledgment or service-level response.

## Decision

Add a separately gated global-service monitor that can run only when the CARE simulation API is configured. `DCX_GLOBAL_CARE_MONITOR=1` enables it, and an optional bounded interval is 100–300,000 milliseconds with a 30-second default. It calls only `ICareOperationsSource.RefreshAsync`; it has no processing call path.

The monitor validates the aggregate health contract, serializes overlapping polls and emits fixed alerts for `retry_scheduled`, `retry_due`, `operator_action_required` and recovery to `clear`. It suppresses initial/unchanged clear state and unchanged delivered state. The fingerprint intentionally excludes the cumulative completed count. A failed sink delivery does not advance the fingerprint, so the next cycle retries; a successful delivery advances it. Restarting the process intentionally re-emits a nonclear state.

The simulation sink logs only a stable alert code and aggregate pending/due/blocked/maximum-attempt counts. It omits exception text, raw health bodies, times, tokens, event/provider/plan/subscription/sale/account/contributor identifiers and free-form messages. Worker failures log one fixed content-free warning.

## Consequences

- Automated evidence now covers bounded cadence, default-off service wiring, state/recovery transitions, unchanged-state deduplication, concurrent polling, sink retry, aggregate-only shape and zero process/provider actions.
- Existing review, approval, transactional audit and replay gates are unchanged; the monitor cannot trigger them.
- Deduplication is process-local. There is no durable schedule/alert state, external notification transport, delivery receipt, acknowledgment, escalation, on-call routing, SLA, dashboard or accessible blocked-event disposition. Production scheduling/alerting therefore remains blocked.
- Logging is not an operator notification system or centralized audit. Deployment, monitoring, retention and human assistive-technology acceptance remain required.
