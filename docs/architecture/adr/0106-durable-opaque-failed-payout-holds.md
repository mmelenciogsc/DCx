# ADR 0106: Persist opaque failed-payout holds and zero-authority decisions

- Status: Accepted durable-state foundation; authenticated local composition is governed by ADR 0107 and external notification remains blocked
- Date: 2026-10-19

## Context

ADR 0105 introduced a generated native review surface, but its one process-memory record did not prove that an authoritative failed or later-returned payout could be projected without exposing the durable remediation tables' contributor, snapshot and destination bindings. A durable operator decision also needs restart, cross-connection concurrency, migration and tamper evidence while remaining strictly separate from payout execution.

## Decision

- Schema v15 adds an immutable binding from each unsuccessful payout item to a random 128-bit lowercase opaque review reference. The binding contains only the internal source batch/ordinal, opaque reference and UTC creation time. It is created in the same transaction as an initial failed item or later return/refund/reversal.
- Startup from schema v14 transactionally backfills missing references for existing unsuccessful items. Every review/list operation rejects a missing reference or a reference attached to a successful item. Future schema versions still fail closed.
- The public projection contains only opaque reference, CARE month, integer-cent amount, closed public failure/hold/notice/disposition codes, UTC failure time and a SHA-256 review digest. The digest binds the internal batch, ordinal and terminal-result digest without exposing them. Contributor pseudonym, destination, recipient snapshot, provider batch/account, transaction ID and free-form text remain absent.
- An item with separately authorized remediation lineage is no longer an open hold. Creating or recording a hold decision never creates that lineage and never reserves, retries, releases, sends or reconciles a payout.
- A separate append-only table permits exactly one `retain_hold`, `request_recipient_update` or `cancel_retry_intent` decision per opaque reference. It records UTC, an operator-pseudonym SHA-256, exact reviewed digest and a sequence/previous/current SHA-256 chain. Exact replay is inert; conflicting, stale, malformed and cross-reference requests fail closed. Update/delete triggers protect both bindings and decisions from ordinary mutation.
- The provider-neutral source returns results whose provider calls, financial mutations, payout attempts and notification transports are all zero. `local_dashboard_notice_ready` is local display state only. No API endpoint or normal desktop composition is added by this decision.

## Validation

Implementation commit `1e3596a` passes 135/135 finance tests and full Windows CI at 660/660 with zero build warnings/errors. Evidence is `artifacts/test-results/p11-53d841edd51b4635a5b99d27db1abcbf/`. Focused cases prove schema-v14 backfill, initial-failure and later-return reference creation, privacy-minimized projection, exact request/result binding, two-repository concurrent replay serialization, conflict and chain-tamper denial, restart persistence, unchanged balanced accounting and removal from the open projection only after real remediation lineage exists. Native schema-v4 UIA continues to prove the bounded interaction and all zero-effect counters.

## Consequences and residual risk

- The authoritative sandbox can now retain a privacy-minimized review identity and non-financial operator intent across restart without copying destination or contributor fields into the review/audit tables.
- Random references and operator digests remain sensitive linkable pseudonymous data, not anonymity. A privileged database administrator can still drop triggers and rewrite/recompute the entire chain.
- ADR 0107 wires the source to a default-off authenticated loopback validation API and isolated desktop fixture. ADR 0108 projects a verified decision to only its bound contributor's read-only local dashboard. Normal desktop startup remains unavailable, and neither composition grants production operator identity, MFA/TLS, role lifecycle, centralized external audit, notification delivery or payout authority.
- Provider-verified recipient eligibility, credentialed PayPal sandbox acceptance, approved accounting/notification policy and human JAWS/NVDA/Narrator acceptance remain blocked.
