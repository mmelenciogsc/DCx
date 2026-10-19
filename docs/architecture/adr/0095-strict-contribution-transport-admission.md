# ADR 0095: Admit contribution transport through strict unambiguous contracts

- Status: Accepted for local simulation hardening; production contribution transport remains blocked
- Date: 2026-10-19

## Context

P10 bounded contribution envelopes and loopback responses and rejected unknown JSON members, but duplicate known properties remained ambiguous. Binary intake accepted media-type parameters/content encoding, same-origin responses did not require the exact requested route, and temporary server read buffers were not explicitly cleared.

## Decision

- Envelope metadata rejects comments, trailing commas, excessive depth, unknown members and duplicate properties at every depth before materialization. Its bounded parse copy is cleared.
- Simulation intake requires the exact parameter-free binary media type, no content encoding, a bounded declared length and the same streamed length. Owned read and accumulation buffers are cleared.
- The Windows simulation client requires the exact requested response URI, unencoded `application/json` with optional UTF-8 charset, independent declared/streaming 16 KiB limits, duplicate-free strict JSON and existing identity/state bindings for success and errors.
- Requests select JSON, identity encoding and no storage. The handler retains redirect/cookie/proxy/decompression denial and adds 16 KiB response-header and two-connection limits.

These rules create no remote authority, production identity or publication capability.

## Validation

Focused contribution policy/client/real-child-Kestrel tests pass 19/19. The P10 aggregate at `artifacts/test-results/p10-153fbaee50d2401b8e7a50f4a3cab757/` passes 53/53 plus native UIA and the rootless hostile-package sandbox. The Windows suite passes 611/611 with zero build warnings/errors.

## Consequences and residual risk

- Existing exact quarantine, reconciliation and withdrawal fixtures remain compatible.
- Clearing owned arrays minimizes memory lifetime; it cannot erase runtime, kernel or socket copies.
- Static bearer roles, loopback HTTP, process-local throttling and same-SQLite queues remain simulation-only. Production identity/TLS, distributed controls, approved Ubuntu deployment/isolation and independent testing remain blocked.

## Alternatives rejected

- Last-property-wins JSON creates ambiguity in security-relevant fields.
- Binary charset/content encoding parameters are unnecessary for an exact byte envelope.
- Origin-only response checks authorize more routes than each fixed contribution action needs.
