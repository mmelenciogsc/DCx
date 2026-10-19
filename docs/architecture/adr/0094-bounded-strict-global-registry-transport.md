# ADR 0094: Admit global registry responses through bounded strict contracts before signature work

- Status: Accepted for client transport hardening; production registry deployment remains blocked
- Date: 2026-10-19

## Context

The global replica authenticates policy, release, catalog and artifact bytes cryptographically, but its HTTP descriptor layer is processed before those signatures can be checked. Direct stream deserialization allowed an enrolled but hostile or compromised endpoint to send an unbounded descriptor, duplicate or unknown fields, ambiguous encodings, or semantically impossible sizes. Chunk endpoints bounded bytes while reading but did not require the documented binary content contract. These gaps could consume memory, create parser differentials or cause unnecessary network contact before eventual signature rejection.

## Decision

Every release and trust-policy descriptor response is admitted before deserialization through all of these checks:

- the response request URI must equal the exact canonical URI constructed from the enrolled origin;
- media type must be `application/json`, optional charset must be UTF-8, and content encoding must be absent;
- declared length and streaming length are independently capped at 16 KiB;
- JSON comments/trailing commas are denied, depth is capped, every duplicate property is denied recursively and every unknown member is denied;
- sequence/revision, SHA-256 and signed-object size must satisfy the same semantic bounds used by the replica client.

Transfer chunks require `application/octet-stream` with no charset or content encoding. Declared and streamed bytes may not exceed the exact requested count. Invalid sequence/revision, offset, count or artifact SHA-256 is rejected before an HTTP request. Stable failures do not include the origin, response body or transport exception text.

Normal product composition continues to disable redirects, cookies, proxies and decompression. It now also enables certificate-revocation checks, caps response headers at 16 KiB and permits at most two connections to the registry. The existing 30-second overall timeout and explicit-only synchronization remain.

Descriptor byte arrays and temporary read buffers are cleared after parsing. This is defense in depth for public metadata, not a claim that managed or network buffers are securely erased.

## Validation

Eighteen focused HTTP-transport cases cover the valid descriptor/chunk path; remote HTTP denial; wrong media types; duplicate/unknown/malformed fields; invalid semantic bounds; declared and unknown-length oversize; changed response URI; non-UTF-8/compressed JSON; binary charset; declared and streamed chunk oversize; and invalid outbound requests with zero handler calls.

The exact-source P09 aggregate at `artifacts/test-results/p09-e01d78b05a3b4529adc2cfca32f531bc/` passes 41/41 plus native UIA against implementation `0b9d254`. It includes a real child Kestrel/SQLite trust-policy and release synchronization, proving the strict client remains compatible with the service. The complete Windows suite passes 611/611.

The same milestone corrected a separate native accessibility defect found by the aggregate gate: WPF default-button semantics exposed carriage return instead of the documented search mnemonic. The default designation was removed, the action is now **Find offline** with `Alt+F`, and UIA asserts every exact Global knowledge and enrollment mnemonic.

## Consequences and residual risk

- An authenticated payload still must pass all existing digest, signature, chain, policy and atomic-activation checks; strict HTTP admission does not replace them.
- A valid enrolled server can still refuse service or respond slowly until the bounded timeout. Availability and production monitoring remain deployment concerns.
- Platform TLS validation and revocation checking are used; this decision does not establish production certificate issuance, pinning, DNS control or key custody.
- The two-connection limit is intentionally conservative because one replica serializes synchronization. It must be remeasured before any future parallel artifact design.
- Docker/Compose execution, production root/origin/TLS ceremony, malicious same-user isolation and human screen-reader acceptance remain blocked.

## Alternatives rejected

- Rely only on signatures after unbounded descriptor parsing: signatures cannot bound work already performed.
- Trust `Content-Length` alone: chunked or dishonest responses require an independent streaming cap.
- Ignore unknown or duplicate JSON fields: this permits schema drift and last-value parser ambiguity at a trust boundary.
- Accept compressed responses: decompression adds an unnecessary expansion surface for tiny descriptors and bounded chunks.
- Follow redirects within the registry origin: the exact enrolled origin and route are the approved network authority.
