# ADR 0081: Compose the authenticated CARE ledger with the native dashboard only in an isolated fixture

- Status: Accepted for native local validation; production contributor authentication remains blocked
- Date: 2026-10-18

## Context

ADR 0080 proved a subject-bound process transport and ADR 0078 proved the native dashboard with generated records, but they did not prove that the actual Windows window could consume the authenticated authoritative-ledger projection. Normal DCx must not treat a static fixture token, loopback HTTP or a generated identity as a production sign-in.

## Decision

Add a dedicated `--care-contributor-loopback-fixture` desktop startup mode. It is available only when an explicit validation environment gate is present with the exact loopback origin, generated bearer token and expected pseudonym. Normal browser startup continues to use the unavailable contributor source.

The fixture constructs the hardened ADR 0080 client, clears the raw token environment variable immediately, opens only the read-only contributor window, labels the source and boundary as authenticated local validation, and disposes the source when the window closes. Disposal clears the owned token bytes and prevents later credential reuse. The window restores heading focus after its asynchronous initial refresh.

Validate the composition by starting the real GlobalService as a child process over literal-loopback Kestrel and an ephemeral SQLite CARE database and P-256 signing key. Start the real desktop as another process, drive its native UIA tree and keyboard access, and require exact authenticated empty-history semantics rather than invented records. Scan service diagnostics for the generated token and pseudonym, then stop both processes and delete the ephemeral key. The validator also proves that the ungated desktop argument exits without exposing a window.

## Consequences

- Native UIA now proves the real ledger-to-transport-to-client-to-window composition, keyboard refresh, focus restoration and truthful zero-history presentation.
- Raw fixture credentials are confined to child-process startup, removed from the launch environment object, cleared by the desktop after construction and zeroed from the client-owned byte buffer on close. Transient managed strings cannot be securely erased.
- The mode makes zero provider request, payout attempt or production action and grants no finance mutation authority.
- Static generated credentials, loopback HTTP, one fixture subject, process environment bootstrap and local diagnostics scanning are not production identity, MFA, TLS, revocation, session management, multi-subject authorization, access audit or deployment security.

## Alternatives rejected

- Configure the contributor client in normal startup: this would make validation credentials look like product authentication.
- Continue testing transport and window separately: this would leave the highest-risk composition and asynchronous focus behavior unproved.
- Populate the ledger merely to make the UI nonempty: an authenticated contributor with no qualified history must receive a truthful empty snapshot, not generated financial data.
