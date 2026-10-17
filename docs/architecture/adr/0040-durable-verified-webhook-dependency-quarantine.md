# ADR 0040: Durable verified-webhook dependency quarantine

- Status: Accepted for uncredentialed handler-backed validation; production operations and provider acceptance blocked
- Date: 2026-10-17

## Context

PayPal can redeliver or deliver webhooks out of dependency order. A completed sale cannot be safely bound before its verified subscription activation establishes the configured-plan mapping, and a refund or reversal cannot be applied before its verified sale exists. Dropping those events loses accounting evidence; repeatedly contacting PayPal from an automatic retry path increases cost and attack surface; accepting an unverified or structurally invalid event would let the queue bypass the official verification boundary.

## Decision

Only an event that has already passed the official sandbox verification adapter and strict event mapping may enter quarantine, and only for the stable missing-subscription-binding or missing-sale-binding reasons. Malformed, unverified, wrong-plan, conflicting and financially invalid events never enter the queue.

CARE schema v8 stores the bounded normalized verified fields, expected plan, canonical payload digest, a digest over the complete normalized record, stable reason/state, attempt count and UTC scheduling times. It stores no raw event body, signature, certificate, credential, access token or provider response. Exact replay is inert; reuse of an event or transmission identifier with different content, including a collision from the deterministic fixture path, fails closed.

Reprocessing is an explicit local database operation. It accepts at most 100 due records, performs no provider request, prioritizes sales before corrections, and uses bounded exponential backoff from two seconds through one hour when a dependency is still absent. Normalization, mapping mutation, webhook audit, entitlement/ledger accounting and quarantine completion share one transaction. A failed normalization transaction is rolled back before retry state is recorded. A now-invalid event becomes blocked under a stable generic reason instead of retrying forever. The stored record digest is verified before every read/reprocess; tamper fails closed without accounting mutation.

A separate read-only health snapshot contains only one stable status code, four aggregate state counts, maximum attempt count, and the oldest-pending/next-attempt UTC times. Identifier-bearing fields are structurally absent. It distinguishes due, scheduled, clear and operator-action-required state without making a provider request or authorizing an action.

## Consequences

- Verified out-of-order sales and corrections survive process/repository restart and can be applied after their dependencies without a second PayPal call.
- Queue completion remains idempotent and uses the same financial, entitlement and replay invariants as in-order processing.
- Completed and blocked normalized records remain sensitive pseudonymous accounting metadata subject to an approved retention, access and subject-rights policy.
- No production scheduler, alerting, accessible operator disposition surface, credentialed webhook delivery or PayPal acceptance exists. This ADR creates no authority to contact PayPal or transact.
