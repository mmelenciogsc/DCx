# ADR 0078: Expose CARE contributor status through a provider-neutral read-only contract

- Status: Accepted for generated local validation; authenticated contributor service remains blocked
- Date: 2026-10-18

## Context

The product specification requires a transparent contributor dashboard. Existing P11 surfaces served the owner/operator: recipient approval, verified-event health and blocked-case disposition. They did not let a contributor inspect monthly allocations, holds, Net 60 eligibility, pending amounts and paid amounts. Wiring the desktop directly to the authoritative finance database would violate the Windows/server boundary and create a new local secret and identity risk.

## Decision

Define a provider-neutral `ICareContributorDashboardSource` in Core. Its bounded snapshot contains only one validated contributor pseudonym, USD, generation time and at most 120 descending monthly records. Each record binds the canonical CARE month, actual month close, close plus 60 calendar days, allocation, paid and outstanding cents, and one closed status: held below threshold, withheld, eligible, pending, partially paid or paid.

The policy rejects malformed identities, duplicates, ordering/date substitution, amount imbalance, premature eligibility and status/amount conflicts. It structurally has no recipient destination, provider account, transaction identifier, raw usage or action field. The native WPF window is keyboard/UIA accessible, read-only and fail-closed. Normal launches use a `not_configured` source and cannot infer an empty history or zero balance. A separately gated deterministic fixture supplies generated data for unit and native UIA validation only.

## Consequences

- Contributors gain an accessibility-tested product surface without giving the desktop finance mutation or payout authority.
- Future authenticated transport must implement this exact minimized contract and separately prove identity, TLS, authorization, freshness, retention and subject-rights behavior.
- The dashboard cannot approve recipients, process events, release holds, create payouts or contact PayPal.
- Generated fixture state is not production contributor data, provider acceptance or evidence that a payout occurred.

## Alternatives rejected

- Display owner/operator records as a contributor dashboard: their purpose and privacy boundary differ.
- Open the server SQLite database from the desktop: this bypasses the service trust boundary.
- Include email, PayPal account or transaction IDs: they are unnecessary for contributor transparency and increase exposure.
- Show unavailable state as an empty history or zero balance: that would fabricate financial information.
