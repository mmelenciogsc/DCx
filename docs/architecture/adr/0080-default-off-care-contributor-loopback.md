# ADR 0080: Prove CARE contributor transport through a default-off subject-bound loopback simulation

- Status: Accepted for local process-boundary validation; production identity and transport remain blocked
- Date: 2026-10-18

## Context

ADR 0079 created an integrity-verified server projection, but the only native dashboard data remained generated. The next safe integration step needs to prove authentication, subject binding, serialization and process separation without calling PayPal, deploying the Ubuntu service, exposing financial data remotely or treating a static fixture token as production identity.

## Decision

Add a read-only simulation endpoint only when the global simulation gate and the separate `DCX_GLOBAL_CARE_CONTRIBUTOR_DASHBOARD_API=1` gate are present with one token SHA-256 and one validated contributor pseudonym. Omission keeps the route absent; partial or malformed configuration prevents startup. The route is prohibited in container mode and the non-container service already requires a literal-loopback HTTP origin.

Authenticate the bounded printable bearer token with a fixed-time digest comparison before reading the CARE repository. Apply the existing bounded per-peer simulation limiter. Return only the ADR 0078 snapshot from the ADR 0079 source, revalidate its available state and exact configured subject, and mark every response `no-store`. The endpoint has no mutation, provider, recipient, payout, approval or operator action.

Add a validation-only Core client that accepts only a root HTTP origin on a literal loopback IP, disables redirects, cookies, proxies and decompression, bounds time and response bytes, rechecks the final origin/media type/strict duplicate-free JSON contract, validates all finance invariants and requires the returned pseudonym to equal its expected authenticated subject. Operator and contributor credentials are role-separated. Normal desktop launches continue to use the unavailable source and cannot discover or instantiate this transport.

## Consequences

- A real child-Kestrel/SQLite test proves missing/invalid authentication denial, operator/contributor role separation, exact-subject binding, empty qualified history, secret-free diagnostics and zero provider action.
- Owned token and response byte arrays are zeroed where possible. Authorization headers necessarily create transient managed strings that cannot be securely erased.
- Static digest configuration, one subject per fixture process and plain loopback HTTP are validation controls only. Production federation/session identity, MFA where applicable, TLS, rotation/revocation, multi-subject authorization, rate limiting, audit, deployment, retention and incident handling remain blockers.
- The generated native fixture still validates accessibility; it is not silently replaced by the process-boundary simulation.

## Alternatives rejected

- Expose the simulation from a container or remote HTTP listener: a fixture bearer token is not adequate protection for financial status.
- Accept a pseudonym in the route, query or body: authentication must select the subject, not caller data.
- Reuse the operator token: read-only contributor access and finance operations require distinct authority.
- Wire the static token into normal desktop configuration: this would misrepresent simulation authentication as a user sign-in design.
