# ADR 0019: bounded simulation contribution pressure

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

The default-off contribution API had strict transport/body admission and role separation, but an enabled process could still spend unbounded authorization and endpoint work on repeated requests. A simulation safeguard must not use bearer credentials as partition identifiers, retain raw peer addresses or imply production reverse-proxy and multi-node abuse protection.

## Decision

Apply one filter to every enabled contribution route before endpoint authorization/body work. Use a fixed one-minute request window and a concurrent-request lease per canonical direct-peer address. Hash the canonical address with SHA-256 for the in-memory key; never derive a key from a credential or expose the key/address in responses.

Default to 120 requests per minute and four concurrent requests. Bound configuration to 10–10,000 and 1–32, cap memory at 1,024 partitions, reclaim expired inactive partitions, reject non-UTC/regressed time and release concurrency through an idempotent disposable lease. Return `429` with only one stable code for rate, concurrency, capacity or time-policy denial.

Keep this limiter coupled to the contribution API gate. With contribution routes absent it is absent and health reports it disabled.

## Consequences

- Missing/invalid credentials consume the same partition budget as authorized requests, limiting brute-force and parser pressure before endpoint work.
- The partition state contains no raw address, token, contribution body or user-selected identity, and child-process diagnostics remain content-free.
- State is per process, resets on restart, deliberately ignores forwarded-address headers and does not coordinate nodes. Production requires trusted ingress identity, distributed throttling, operational policy/telemetry and independent abuse/security validation.
