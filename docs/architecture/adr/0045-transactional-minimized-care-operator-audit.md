# ADR 0045: Chain authenticated CARE simulation decisions in the finance transaction

- Status: Accepted for default-off local simulation; production identity and centralized audit remain blocked
- Date: 2026-10-18

## Context

ADR 0044 added an authenticated loopback-only CARE operations boundary, but a valid operator's processing decisions were not durably attributable. Logging bearer tokens, raw requests, provider identifiers or quarantined-event identifiers would create unnecessary secret and personal-data exposure. Writing a success audit after the finance transaction would also permit completed work without matching evidence, while writing before it could record work that later rolled back.

## Decision

Require a separately configured SHA-256 operator pseudonym whenever the CARE simulation API is configured. It is independent of the bearer-token verifier and never grants authority. Record only schema-valid process decisions: approval required, capability disabled, stale review, nothing due, or processed. Authentication failures and malformed bodies do not enter the finance database.

Schema v9 appends the UTC time, operator-pseudonym digest, fixed action/decision codes, digest of the reviewed aggregate state and approval, optional digest of the aggregate result, previous-record digest and record digest. It stores no token, request body, event/provider/plan/subscription/sale/account/contributor identifier or raw health/result body. A successful decision is appended in the same immediate SQLite transaction as replay and accounting. Expected denials append and commit without changing queue/accounting state. Unexpected replay failure rolls back both work and its would-be audit.

Records form a sequence-bound SHA-256 chain from a fixed genesis value. Repository reads and every append validate the complete chain. SQLite triggers deny ordinary update/delete operations. Unknown future CARE schemas continue to fail closed.

## Consequences

- Restart, replay, denial, success, cross-process transport and transaction rollback now have durable minimized operator-decision evidence.
- The operator pseudonym is an unsalted, linkable digest and remains sensitive pseudonymous data. Access, retention, notice and subject-rights policy are not approved.
- The chain and triggers detect accidental or ordinary database mutation; they are not an external signed anchor. A database administrator can drop triggers, rewrite and recompute history, or truncate the tail. Production requires real identity/MFA, role lifecycle, protected secret custody, centralized immutable/exported audit, monitoring and approved retention.
- Full-chain validation on append is appropriate for this low-volume simulation but is not a production scale or latency result.
- This changes no provider boundary. PayPal calls, credentials, subscriptions, payments, payouts, deployment and desktop normal-mode activation remain zero.
