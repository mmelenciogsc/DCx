# ADR 0065: Expose local-dependency transfer only through accessible one-time exact-plan approval

- Status: Accepted for the inert acquisition workflow; real acquisition remains blocked
- Date: 2026-10-18

## Context

ADRs 0061–0064 authenticate the catalog and plan, one response stream, confined atomic set publication and protected reverification. Those boundaries were intentionally disconnected from the desktop. P06 still needs a keyboard and screen-reader usable workflow that makes provider contact explicit, reports useful progress, supports cancellation and performs abandoned-partial cleanup without racing active work.

## Decision

The local-model window exposes one initially unchecked approval bound to the exact displayed plan digest, sources, byte totals and storage requirement. Only that approval enables **Begin approved transfer**. Approval is single-use and resets after completion, cancellation or failure. Provider contact is never automatic.

The coordinator serializes transfer, verification and cleanup in process. Progress reports only stage, item ordinal/count and byte counts; it omits source URLs, local paths and transport diagnostics. Cancellation remains keyboard available during work and removes only the current strict random incoming directory. Atomic publication is labeled published, not completed; completion is announced only after protected receipt verification and full payload rehash. Separate **Verify completed set** and **Recover abandoned partials** actions are keyboard reachable. Recovery runs while normal DCx owns its exclusive per-user data-root lease, recognizes only strict incoming names and refuses links. Read-only verification remains available even when current free space is insufficient for a new transfer.

The production transport continues to deny redirects. The UI says so. The current official source addresses therefore cannot complete acquisition until a reviewed direct-origin or narrowly authenticated redirect policy exists. Transfer publication remains inert and grants no extraction, installation, registration, selection, execution, legal or redistribution authority.

## Consequences

- A user can review and authorize the exact network operation, cancel it, hear bounded progress, reverify a completed set and invoke safe recovery without using a pointer or screen-reader browse workaround.
- A gated synthetic native UIA fixture proves default-off approval, unique access keys, focus movement, cancellation, completion, reverification and recovery with provider requests/downloads/installs/executions all zero.
- The fixture coordinator is available only through an explicit command-line/environment/root gate and stores no production authority.
- Human JAWS, NVDA and Narrator acceptance, real source redirect policy, contained-license review and owner-authorized real transfer remain P06 blockers.
