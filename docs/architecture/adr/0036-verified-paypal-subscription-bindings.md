# ADR 0036: Verified PayPal subscription bindings

- Status: Accepted for uncredentialed handler-backed validation; credentialed acceptance blocked
- Date: 2026-10-17

## Context

PayPal webhook-signature verification authenticated a raw event object, while the CARE ledger previously accepted a separately caller-normalized event. That seam permitted field substitution by an integrating caller and did not bind subscription payments or corrections to the configured plan and original sale.

## Decision

Parse duplicate-free bounded event JSON once. Send that immutable object to PayPal's sandbox verification endpoint and return no mapped value unless the response is exactly `SUCCESS`. Admit only the documented subscription lifecycle and sale event types with exact resource type, status, identifiers, RFC 3339 time, USD amount and canonical cents.

Bind lifecycle events to one configured plan ID. Require that durable binding before accepting a completed US$3 sale. Bind the sale ID to its subscription. Resolve refunds and reversals through the recorded sale, reject subscription substitution and cap cumulative corrections at both sale gross and aggregate recorded receipts. Persist mapping, replay, entitlement and accounting changes in one SQLite transaction. Live mode remains absent.

## Consequences

- A caller cannot supply different ledger fields after event verification.
- Payment delivered before a matching lifecycle binding fails closed and needs a future reviewed retry/quarantine workflow.
- Provider subscription, plan, sale and correction identifiers become sensitive pseudonymous accounting data.
- Handler-backed tests do not establish PayPal delivery shape, credentials, merchant/Payouts eligibility or product/plan configuration; credentialed sandbox acceptance remains blocked.
