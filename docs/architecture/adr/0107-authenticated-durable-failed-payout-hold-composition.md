# ADR 0107: Compose durable failed-payout holds through the authenticated local operator boundary

- Status: Accepted for controlled local validation only
- Date: 2026-10-19

## Context

ADR 0106 persisted privacy-minimized schema-v15 failed-payout holds and one zero-authority operator decision, but deliberately left the durable source in process. The generated native surface proved keyboard and accessibility behavior without proving that the same bounded record could cross an authenticated process boundary, survive a real SQLite write, and return to the native window without gaining payment or notification authority.

## Decision

- Reuse the existing default-off CARE operator API, operator token digest, separate operator-pseudonym digest, rate limiter and hardened literal-loopback client. No second credential, cookie, browser session or provider-authentication path is added.
- Add authenticated list and exact-disposition routes for the existing `CareFailedPayoutReview` contract. The server authenticates before repository access, requires route/body identity equality, strict duplicate-free bounded JSON and explicit approval, and maps only stable denial codes. Every CARE operator response now carries `Cache-Control: no-store`, `Pragma: no-cache` and `Vary: Authorization`.
- The client continues to disable redirects, cookies, proxies and decompression; pins the exact literal-loopback origin and response URI; requires unencoded JSON with optional UTF-8 charset; bounds headers, time and declared/streamed response bytes; rejects unknown/duplicate fields; and revalidates every review/result invariant. A response with any provider call, financial mutation, payout attempt or notification transport is rejected.
- Normal desktop startup remains unavailable. A separate environment-gated command-line fixture constructs the hardened client, clears the raw token environment variable immediately, labels the boundary as local synthetic validation rather than production authentication, owns/disposes the client with the window, and disables a decided record's controls.
- A test-only console seeder creates one synthetic schema-v15 partial payout through the real period-close, liability, reservation and completion APIs. It requires one empty validation root and exposes no provider adapter. The UIA harness uses a random ephemeral P-256 simulation key, deletes it before retaining evidence and verifies absence again during cleanup.
- `request_recipient_update` means only that an accessible local dashboard notice is ready. The transport has no contributor contact address, external notification adapter, retry/release action, payout provider or production deployment authority.

## Validation

Implementation `a297811` and key-erasure hardening `a23207d` pass a locked Windows Release build with zero warnings/errors, 137/137 finance tests and the full 662/662 suite. Evidence is `artifacts/test-results/p11-9cb78d51e3e747a0a08d5758544bd0b8/`.

The real child-Kestrel/SQLite and native UIA path proves default-off launch denial, operator/contributor role separation, minimized durable listing, initially unchecked approval, denied-attempt focus return, exact route/review binding, stale/conflicting denial, idempotent exact replay at the service, disabled replay in the UI, restart-safe reread and zero provider/financial/payout/notification effects. `operations-loopback-uia.json` records zero production actions and `ephemeral_signing_key_retained=false`; the key file is absent.

## Consequences and residual risk

- The authenticated local validation gap for durable hold review is closed without connecting the normal desktop or adding provider authority.
- The bearer token, opaque review reference, timestamps and operator digest remain linkable sensitive pseudonymous data. The client zeros owned token/response byte arrays, but managed strings, child-process environment bootstrap, OS networking and UIA copies cannot be securely erased.
- Static token digests, literal-loopback HTTP, process-local limiting, environment-gated fixture startup and same-machine SQLite are not production identity, MFA, TLS, secret rotation/revocation, distributed authorization, externally anchored audit or malicious same-user isolation.
- External contributor notification, retention/access/subject-rights policy, authenticated production operator lifecycle, deployed service acceptance, credentialed PayPal sandbox acceptance and human JAWS/NVDA/Narrator testing remain blocked.
