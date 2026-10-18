# ADR 0085: Bind Codex cancellation from exact turn notifications

- Status: Accepted for the local app-server boundary; real provider acceptance remains blocked
- Date: 2026-10-19

## Context

ADRs 0070 and 0083 retained the identity-bearing `turn/start` response and bound cancellation synchronously in the response-reader path. A later exact-source full-suite run still observed a completed turn. The fixture delayed the response and, like a strictly serial server, could leave an interrupt unread while processing that request. More importantly, the app-server protocol can disclose the exact thread and turn in `turn/started` or later turn notifications before the correlated response arrives. Ignoring that identity delayed a cancellation that was already requested.

## Decision

Before dispatching `turn/start`, register one bounded pending cancellation intent against the exact unique ephemeral thread. Bind it to the first exact identity-bearing `turn/started`, delta or completed notification, and send `turn/interrupt` as soon as both cancellation intent and exact thread/turn identity exist.

The later correlated response remains mandatory and must identify the same thread and turn. A conflicting response, a turn reused across threads or any ambiguous mapping fails closed. Response-reader binding from ADR 0083 remains a consistency check and fallback when no earlier notification carries identity. Cleanup removes the pending mapping and turn state.

The offline fake server processes delayed requests asynchronously so its stdin loop can receive an interrupt while the original request is pending. Its delay exceeds the uncancelled completion time, making the previously unsafe ordering deterministic in the test.

## Consequences

- Cancellation no longer waits for a delayed `turn/start` response when an exact notification already identifies the active turn.
- Interrupt authority remains restricted to one exact ephemeral thread and turn; DCx never interrupts a guessed or latest turn.
- Conflicting notification/response identities are rejected rather than silently rebound.
- Focused tests and the complete offline suite cover the observed ordering without credentials, provider network access or billable work.
- This supersedes only the earlier assumption about which protocol message arrives first. The identity-retention and response-reader rules in ADRs 0070 and 0083 remain required.
- Real provider behavior and commercial ChatGPT sign-in eligibility remain unverified and unauthorized.

## Alternatives rejected

- Wait only for the correlated response: a valid early notification can provide exact interrupt authority sooner.
- Interrupt by thread without a turn ID: concurrent or reordered work makes the target ambiguous.
- Accept a later conflicting response: that would permit cancellation and result handling to refer to different turns.
- Keep the fixture's request loop blocked: it would test an artificial transport deadlock instead of full-duplex JSONL behavior.
