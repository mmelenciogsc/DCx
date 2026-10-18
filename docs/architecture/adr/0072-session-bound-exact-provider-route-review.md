# ADR 0072: Review exact provider routes in a short-lived user-initiated session

- Status: Accepted for the inert dependency workflow; real payload transfer remains owner-gated
- Date: 2026-10-18

## Context

ADR 0071 proved that the authenticated P06 sources currently resolve through signed, time-varying GitHub and region-specific Hugging Face URLs. A compiled global CDN prefix would be broader than the observation and could become stale in another region. Keeping the product permanently deny-all would also leave no safe path from the accessible transfer review to the already hash-pinned objects.

Route observation must remain different from payload approval. It must not run at startup, retain bearer-like query values, read response bodies, persist network authority, or silently authorize a later transfer.

## Decision

The normal local-model window starts with the canonical deny-all redirect review. It exposes a separate keyboard action, **Observe current provider routes**, whose invocation is the explicit authority for a bounded route check. The action reloads the authenticated embedded catalog, re-derives the exact plan and requires a plan-bound schema-v1 review approval before any request.

The reviewer uses the hardened native HTTP handler with automatic redirects, proxies, credentials, preauthentication, cookies and decompression disabled. It sends `HEAD` with identity encoding and no-store, follows at most three safe nonrepeating HTTPS redirects per exact item, requires final HTTP 200, rejects conflicting final length metadata, and never reads a response body. Caller cancellation and a two-minute total deadline remain available. Stable errors omit transport details.

Each accepted transition is canonical redirect-policy schema v2. It binds the exact item source, length and SHA-256 plus the exact query-free previous and target HTTPS URIs. It does not authorize a host or directory prefix. Signed query values remain only in transient request URIs and are absent from the review, UI, receipt and error text. At most 32 transitions are admitted, enough for the current nine-item, three-hop-bounded plan.

A successful observation creates a non-persisted two-hour session tied to the exact plan. The accessible preflight shows its HEAD/request/redirect/length counts, expiry, canonical policy digest and every exact query-free transition. The transfer checkbox stays unchecked and is unavailable until this separate review succeeds. Starting a transfer still requires the exact displayed plan-and-policy approval and a fresh capacity check. Expiry changes the live policy back to deny-all; stale approval is rejected before a new payload request, and a change during an admitted transfer triggers the existing total rollback boundary.

The independent command-line route audit remains evidence-only and cannot create a product session. Synthetic core and native UIA gates invoke no provider. No automatic acquisition, extraction, staging, installation, registration, selection or execution is added.

The clean-tree product validator subsequently exercised the actual reviewer against the current nine-item plan. It observed 12 HEAD requests, three redirects and eight exact lengths, produced three exact query-free schema-v2 transitions, read no bodies, issued no payload GET, created no transfer approval or coordinator, and persisted no session. This validation does not broaden the decision or grant payload authority.

## Consequences

- DCx no longer needs a broad, globally compiled CDN prefix to cope with dynamic regional routing.
- The exact official source controls the observed redirect, while platform TLS, item identity, exact length and SHA-256 remain mandatory; this still does not establish license suitability or redistribution rights.
- Route review itself discloses the public source request and client network address to those public providers. The UI describes that contact before invocation and retains no query or account data.
- A changed route, unsupported HEAD response, missing review, expiration, cancellation or policy substitution fails closed. The user can explicitly review again.
- This design closes the route-mechanics blocker. It does not authorize this development run's 1.28 GB payload transfer or satisfy contained-license, real Whisper/embedding benchmark, human screen-reader, packaging or legal gates.
