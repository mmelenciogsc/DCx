# ADR 0079: Derive CARE contributor status from the verified authoritative ledger

- Status: Accepted for server-side local validation; authenticated contributor transport remains blocked
- Date: 2026-10-18

## Context

ADR 0078 defined a minimized, provider-neutral contributor dashboard contract and an accessible generated-data surface. It did not define how authoritative CARE data becomes that contract. Reading allocations or payout rows independently could expose a mixed snapshot, trust edited derived values, double-count remediation batches, or report a payout state whose close approval, allocation, preview or terminal result no longer verifies.

## Decision

Project the dashboard inside `CarePeriodAllocationRepository` under the repository mutation lock and one non-deferred SQLite transaction. Read at most 120 newest positive contributor allocations. For every included period, revalidate the registered schema-v14 close review and approval, canonical economics, qualified weights, deterministic allocation and hold decision before using it.

Read at most 1,000 bound payout batches per period. Revalidate each bounded stored preview, exact preview digest and total, allocation binding, failed-item remediation lineage, terminal item order/content/result digest and paid total. Treat pending reservations as pending, successful terminal items as paid, and returned/refunded/reversed items as no longer paid. Derive, rather than store, the closed status with this precedence: held below threshold; fully paid; pending; partially paid; withheld before Net 60; otherwise eligible. An authenticated contributor with no qualified history receives an available empty snapshot; an unauthenticated or unconfigured caller still receives unavailable state.

Expose this projection through a server-side `ICareContributorDashboardSource` that is given an already authenticated contributor pseudonym and UTC clock. Do not add a desktop database read, HTTP endpoint, authentication scheme or production deployment as part of this decision.

## Consequences

- One coherent read cannot mix allocation and payout states across cooperating repository mutations.
- Close/allocation/preview/result tampering and invalid remediation lineage fail closed instead of producing financial UI state.
- Reopen behavior and held, withheld, eligible, pending, partial, paid and post-return transitions are deterministic.
- The normal desktop source remains unavailable, and the existing generated fixture remains only an accessibility test. Production identity, authorization, TLS, freshness, rate limits, retention, contributor rights and transport acceptance are still required.

## Alternatives rejected

- Query allocation and payout tables without integrity revalidation: stored derived rows are not an independent trust root.
- Build status from the latest payout batch only: partial payments, concurrent reservations, remediation and returns require the complete bounded period history.
- Let the Windows client open authoritative SQLite: that violates the server trust boundary.
- Add a simulation bearer endpoint and call it production authentication: local fixture credentials do not establish contributor identity or deployment security.
