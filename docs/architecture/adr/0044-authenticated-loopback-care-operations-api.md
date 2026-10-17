# ADR 0044: Authenticated loopback CARE operations API

- Status: Accepted for default-off local simulation; production identity and deployment blocked
- Date: 2026-10-18

## Context

ADR 0043 made reviewed CARE replay atomic for connections to one SQLite database, but no process-boundary transport exercised that source. Wiring the desktop directly to the database, treating a fixture token as production identity or enabling processing with the general simulation service would weaken the trust boundary.

## Decision

Add two aggregate-only endpoints under `/api/care/operations`: authenticated health refresh and approved processing. They exist only when `DCX_GLOBAL_SIMULATION=1` and the separate `DCX_GLOBAL_CARE_OPERATIONS_API=1` gate are present. Configuration contains only the SHA-256 digest of a bounded operator bearer token. Mutation authority requires the additional `DCX_GLOBAL_CARE_PROCESSING=1` gate; API enablement alone leaves processing disabled.

The API uses the existing privacy-minimized per-peer request limiter with a dedicated CARE response adapter. Authentication hashes supplied token bytes and compares in fixed time. Requests require exact bounded `application/json`; unknown or inconsistent fields fail closed. Responses contain only the Core aggregate health/result shapes or stable error codes. Bodies, bearer values, provider identifiers and exception text are not logged.

Add a validation-only Core client restricted to an HTTP root origin on a literal loopback IP. Redirects, cookies, proxies and decompression are disabled; time and response bytes are bounded; response origin, media type, exact JSON fields, aggregate invariants and zero provider calls are revalidated. The retained token copy is owned bytes and is zeroed on disposal, although constructing an HTTP authorization header necessarily creates a transient managed string that cannot be securely erased.

Normal desktop launches continue to use `UnavailableCareOperationsSource`. No setting, environment variable or automatic discovery wires this simulation client into the Windows UI.

## Consequences

- A child-Kestrel E2E test proves missing/invalid authentication, malformed input, approval denial, stale review, exact one-time replay, replay denial, unchanged provider-call count and secret-free diagnostics.
- Client tests deny non-literal/non-loopback origins, response-origin substitution, unknown fields and nonzero provider-call results.
- The service remains globally default off, and health evidence proves the CARE API, processing capability and limiter are absent when not configured.
- This is not TLS, production operator identity, role lifecycle, MFA, durable operator audit, secret-vault integration, distributed fencing, deployment acceptance or PayPal acceptance.
