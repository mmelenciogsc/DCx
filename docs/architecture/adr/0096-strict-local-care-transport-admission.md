# ADR 0096: Admit local CARE traffic through exact strict contracts

- Status: Accepted for local sandbox hardening; production finance transport remains blocked
- Date: 2026-10-19

## Context

The default-off CARE operations and contributor-dashboard simulations already used literal loopback, bearer-to-role/subject binding, body limits and closed schemas. Operations responses still accepted duplicate known JSON fields, both clients accepted any same-origin response route and encoded/non-UTF-8 JSON, and mutation requests allowed duplicate fields and loose content parameters.

## Decision

- Both validation-only clients construct exact absolute routes and require the response request URI to match byte-for-byte. Responses must be unencoded `application/json`, optionally UTF-8, under independent declared and streaming 64 KiB limits.
- Success and stable-error JSON reject comments, trailing commas, excessive depth, unknown members and duplicate properties recursively before materialization.
- Requests select JSON, identity encoding and no storage. Redirects, cookies, proxies and decompression remain disabled; response headers are capped at 16 KiB and connections at two.
- Server mutation requests require unencoded JSON with no parameter or one UTF-8 charset, exact declared/streamed length and duplicate-free strict JSON before policy or finance work.

This changes no default-off gate, normal desktop composition, provider adapter or financial authority.

## Validation

Focused client/server tests pass 29/29, including a real child Kestrel/SQLite service. The complete P11 gate at `artifacts/test-results/p11-d511791678374e39932b500a77d4bbcd/` passes 127/127 plus native recipient, operations, blocked-case and contributor-dashboard UIA. Provider calls, payouts and production actions are zero. The full Windows suite passes 616/616 with zero build warnings/errors.

## Consequences and residual risk

- Existing exact review, approval, subject binding and zero-provider-call invariants remain unchanged.
- Static token digests and literal-loopback HTTP remain validation-only, not production identity, TLS, MFA, rotation, distributed throttling or deployment security.
- Credentialed PayPal sandbox acceptance, product/plan provisioning, merchant/recipient eligibility, accounting/legal approval and human AT remain blocked.

## Alternatives rejected

- Same-origin-only response checks grant more route authority than a fixed finance action requires.
- Last-property-wins JSON permits ambiguous reviewed health, approval and disposition fields.
- Accepting compressed or non-UTF-8 JSON adds unnecessary parser/expansion behavior to small bounded contracts.
