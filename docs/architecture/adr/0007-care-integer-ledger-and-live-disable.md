# ADR 0007: Use integer-cent CARE accounting and structurally disable live payouts

- Status: Accepted for sandbox implementation
- Date: 2026-10-17

CARE calculations and double-entry postings use checked integer US-dollar cents. Every journal event is balanced and idempotent by event ID. The contributor-pool rate is validated at no more than 4,000 basis points of positive defined distributable net profit; cent allocation uses deterministic largest remainder.

Payout previews carry an explicit mode. The coordinator accepts only `Sandbox`, requires verified eligibility, explicit owner approval and the Net 60 date, and rejects a duplicate batch. `Live` always throws; there is no live provider implementation or fallback.

This decision does not establish the final accounting policy. A durable immutable server ledger, PayPal sandbox credentials, actual webhook-verification API, provider reconciliation, account eligibility, tax/regulatory analysis and owner approval are still blockers.
