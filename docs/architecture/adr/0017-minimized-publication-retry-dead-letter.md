# ADR 0017: minimized publication retry and dead-letter state

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

P10 persisted reviewer-authorized intents and fenced same-SQLite execution, but recovery ran only synchronously at startup or by explicit method call. A transient registry failure could repeatedly interrupt startup, and there was no bounded retry schedule or minimized operator-visible terminal state. Retry machinery must not copy candidate bodies or expand reviewer authority.

## Decision

Add a hosted worker behind `DCX_GLOBAL_CONTRIBUTION_WORKER=1`, valid only when the authenticated simulation API is also enabled. When active, startup initializes governance without synchronous publication; the worker immediately polls and then repeats at a bounded interval. Each cycle reads at most 100 due intents and uses the existing ownership-fenced lease and registry operation identifier.

Persist only operation/submission identifiers, consecutive-failure count, stable code and retry/dead-letter/remediation timestamps. Use exponential delay capped at five minutes. Dead-letter after five consecutive transient failures or one permanent policy failure. A reviewer may reschedule a dead letter, but the original intent, reviewer, candidate, canonical change and operation identifier remain immutable. Successful completion resolves failure state inside the contribution-state transaction.

## Consequences

- Restart no longer requires synchronous publication when the worker gate is active; malformed queue state still fails closed.
- Queue and diagnostic surfaces omit candidate/artifact bodies, exception text, stacks and credentials.
- The worker is separately default-off, reports its state in health and refuses to start without the contribution API.
- This same-SQLite simulation is not a production message broker, alerting system, operator console, retention policy, multi-node coordinator or high-availability design.
