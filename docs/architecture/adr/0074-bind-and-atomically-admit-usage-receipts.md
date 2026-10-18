# ADR 0074: Bind and atomically admit qualified usage receipts

- Status: Accepted for local contribution simulation; production identity and untrusted-client verification remain blocked
- Date: 2026-10-18

## Context

P10 usage receipts carried an installation pseudonym and entitlement ID, and an installation key authenticated the receipt bytes. The authoritative evaluation context supplied the key and an entitlement-active flag but did not identify the installation or entitlement expected for that key. A compromised client could therefore sign receipts under alternate pseudonyms to divide one installation's daily usage across multiple rate buckets.

Evaluation also read replay state and the daily count before a separate insert. The per-service semaphore did not coordinate two service instances using the same SQLite database. Concurrent valid receipts could both observe the last available daily slot, and concurrent replay could return two qualified decisions even though a unique constraint retained one row.

## Decision

The authoritative context now binds the exact expected installation pseudonym and entitlement ID alongside the actor, bounded installation key, entitlement status and exact UTC evaluation time. Receipt policy requires exact ordinal identity equality before eligibility, self-use, execution and daily-cap qualification. Invalid authoritative context fails closed; malformed receipt or signature input never reaches storage qualification.

Evaluation verifies the bounded HMAC before database work, then opens an immediate SQLite transaction spanning replay lookup, approved-resource lookup, daily qualified-count lookup, decision and insert. Replay returns one non-qualified decision without inserting another row. Every other validly authenticated decision is inserted exactly once with its stable code before commit; a write that does not affect exactly one row fails closed.

The cap remains 100 qualified events per installation pseudonym and resource per UTC occurrence day. Two service instances sharing one database therefore serialize at the database boundary. At slot 100 exactly one of two distinct concurrent receipts qualifies and the other is durably classified `rate_anomaly`. A concurrent identical receipt produces one qualified decision and one `replay` decision.

## Consequences

- Rotating a receipt's installation pseudonym or entitlement ID while reusing an otherwise valid installation key produces `identity_mismatch`, not qualified usage.
- Receipt and nonce uniqueness, resource approval, contributor self-use exclusion, verified-success evidence, time bounds and daily anomaly limits are evaluated from one consistent database state.
- Stored rows remain pseudonymous and bounded: receipt/nonce, installation, entitlement, resource/version, occurrence UTC, qualified flag and stable decision code. They contain no prompt, response, path, URL or artifact body.
- An HMAC and exact context binding still do not prove that an untrusted customer device performed useful work. Production requires an approved identity/key-provisioning and rotation design, authoritative entitlement lookup, anomaly analysis, audit sampling, retention/access policy and external acceptance.
- This decision adds no public receipt API, remote deployment, contributor payment, production publication or server authority.

## Alternatives rejected

- Trust the installation pseudonym carried by a valid HMAC: a key holder can choose that field and split rate buckets.
- Rely on unique receipt/nonce constraints alone: they prevent duplicate rows but did not prevent two qualified return values or a distinct-receipt cap race.
- Use only the existing object semaphore: separate service instances have separate semaphores.
- Start a deferred transaction: both evaluators can read the same pre-insert count before competing to upgrade; an immediate transaction establishes write serialization before those reads.
