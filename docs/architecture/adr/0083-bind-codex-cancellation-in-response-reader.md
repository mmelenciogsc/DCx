# ADR 0083: Bind Codex cancellation intent in the protocol response reader

- Status: Accepted for the local app-server boundary; real provider acceptance remains blocked
- Date: 2026-10-18

## Context

ADR 0070 retained the identity-bearing `turn/start` response after dispatch. A full-suite run still exposed a narrower scheduler race: the protocol reader completed the response task, a later `turn/completed` notification removed active state, and only then did the awaiting continuation bind the already-requested cancellation. The response existed, but task-continuation scheduling allowed terminal notification handling to overtake cancellation binding.

## Decision

Store a bounded response observer with the pending request. Register user cancellation intent before dispatch. When the protocol reader parses the exact correlated `turn/start` response, invoke the observer synchronously on that reader path to bind the returned thread/turn identity and start exact `turn/interrupt` handling before the reader processes later notifications.

The ordinary awaited response remains the source of the caller's result. Observer failure follows the existing pending-request failure path. Timeout, app-server lifetime cancellation and exact identity checks remain mandatory.

## Consequences

- A terminal notification cannot overtake binding of cancellation intent for the response immediately before it.
- Only the exact returned thread and turn can be interrupted.
- Focused cancellation tests pass repeatedly under the previously observed ordering, while the offline fixture uses no credential, network or billable request.
- This does not prove behavior against a real provider or authorize account use.

## Alternatives rejected

- Schedule the observer as another task continuation: it preserves the same overtaking window.
- Interrupt the latest active turn: concurrent or reordered turns make that unsafe.
- Ignore a completed turn: doing so can silently convert a user cancellation request into an accepted completion.
