# ADR 0066: Follow local-dependency redirects only through an exact deny-by-default policy

- Status: Accepted for the transport framework; static prefix rules are superseded by ADR 0072
- Date: 2026-10-18

## Context

ADR 0063 rejected every redirect because the audited GitHub and Hugging Face catalog addresses may return a different download origin. Enabling automatic HTTP redirects would hide the chain, permit unbounded or cyclic transitions, and make ambient credential/header behavior difficult to prove. Hard-coding an unverified provider host would invent an integration contract. DCx needs the safe mechanism before it can review and authorize any real provider-specific route.

## Decision

Keep `SocketsHttpHandler.AllowAutoRedirect` disabled. The dependency transport follows a redirect only through its own bounded loop and an injected policy. Normal production construction supplies a deny-all policy. No GitHub, Hugging Face, Ollama or other redirect rule is currently enabled.

The original framework bound the authenticated item to a source origin and target origin/path prefix. ADR 0072 tightens the current schema to exact query-free previous and target URIs obtained only through an explicit short-lived review session. Rule construction rejects malformed, root and duplicate transitions. Each redirect target must be an absolute default-port HTTPS DNS name with no user information or fragment; path, query and total URI lengths are bounded; IP literals, backslashes, control characters and doubled path separators are denied. Relative targets are denied.

The transport reconstructs a GET for every hop with only identity encoding and no-store. It forwards no authorization, cookie or referrer header. The platform handler has no credentials, proxy, cookies, decompression or preauthentication and bounds response headers. Every response must identify the exact URI requested. At most three policy-approved redirects are permitted, and a repeated URI fails as a loop. The final response still must satisfy the pinned byte length, SHA-256 and response-shape checks. Any denial or failure rolls the destination back to empty and exposes only a stable code.

The policy seam is internal and test-injected. Synthetic tests prove one allowed path and deny default policy, changed item identity, broad/duplicate rules, unsafe targets, loops, excess hops, response substitution and ambient-header forwarding. These tests make no provider request and do not verify any real provider contract.

## Consequences

- The redirect mechanism can be validated without weakening the production deny-all state.
- ADR 0072 derives exact non-persisted routes from current behavior only after an explicit user action; startup and missing/expired sessions remain deny-all.
- Content length and digest checks limit payload substitution but do not replace TLS, official source/provenance review, contained-license review or backend acceptance.
- P06 remains blocked. This decision authorizes neither provider contact nor download, extraction, staging, installation, registration, selection or execution.
