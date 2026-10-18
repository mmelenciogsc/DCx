# ADR 0063: Authenticate and roll back every local-dependency transfer

- Status: Accepted for inert core transport; production acquisition and provider redirects remain blocked
- Date: 2026-10-18

## Context

ADR 0061 binds the reviewed dependency set and capacity formula but intentionally performs no transfer. A downloader that trusted only the self-consistent plan digest would be insufficient: that digest is not signed, and an arbitrary plan could be recomputed. Large responses also require streaming bounds and reliable removal of partial output. Current official GitHub and Hugging Face source addresses may redirect, while DCx has not authenticated a stable direct origin or an acceptable bounded redirect chain.

## Decision

Add an inert per-item HTTP transfer boundary. Production use reloads the hash-pinned embedded catalog and re-derives the selected plan before it accepts an operation. Every operation additionally requires a default-denied approval bound to the plan digest and the exact source URI, byte length and SHA-256; an observed capacity pass is mandatory. The destination must be a new, empty, seekable stream so failure can truncate it deterministically.

The production handler uses HTTPS with platform certificate validation and disables automatic redirects, proxies, cookies and decompression. It requests identity encoding and no-store behavior. It accepts only status 200 whose final URI exactly equals the pinned URI, with the exact declared length and no content range, content encoding or transfer encoding. Streaming is capped at the expected length, hashes bytes incrementally and succeeds only when length and digest match. Cancellation, timeout, transport failure, short/extra content and digest mismatch truncate and flush the destination; public error messages are stable codes without transport details.

The core chooses no filesystem path and does not persist approval, extract, install, register, select or execute content. The native manager exposes the policy as read-only UIA text and deliberately offers no transfer action. Automated tests inject a synthetic trusted catalog and handler; they make no external request.

## Consequences

- A self-consistent forged plan cannot become a production transfer because the embedded catalog is independently authenticated and the plan is re-derived.
- No partial output is retained by a conforming destination after a denied, failed, timed-out or canceled operation.
- Official addresses that redirect fail closed. DCx must not enable real acquisition until stable direct origins or a separately authenticated, narrowly allowlisted redirect policy are verified against official provider contracts.
- A future coordinator still needs confined private destination creation, fresh capacity recheck, per-item protected receipts, atomic set completion, cleanup/recovery, accessible cancellation/progress and explicit user approval. This ADR creates none of those authorities.
