# ADR 0101: Evaluate usage-receipt velocity from authoritative server time

- Status: Accepted for local contribution simulation; production fraud detection and policy approval remain blocked
- Date: 2026-10-19

## Context

ADR 0074 bound a receipt to its authoritative installation and entitlement and atomically enforced a 100-event installation/resource/occurrence-day cap. That closes pseudonym substitution, replay and the final-slot race, but occurrence time is client-claimed. A compromised client could spread signed receipts across resources or backdate them into separate occurrence buckets. One resource/day counter is therefore a useful limit, not sufficient anomaly detection for an untrusted-client threat model.

## Decision

Retain the existing resource/day counter and add four privacy-minimized activity signals: qualified events for the installation in the trailing minute, qualified events for the installation in the authoritative UTC day, qualified events for the entitlement in that day, and distinct resources used by the installation in that day. Their respective closed local-simulation ceilings are 120, 2,000, 10,000 and 128. Reuse of an already-seen resource does not violate the distinct-resource ceiling.

Store an authoritative `evaluated_utc` beside client-claimed `occurred_utc`. Compute all five signals, decide and insert inside the existing immediate SQLite transaction. Count all later stored evaluation times in the trailing-minute query so a small server-clock regression fails closed instead of reopening capacity. Expose and persist only the common stable code `rate_anomaly`; do not reveal which ceiling fired.

Migrate an existing local simulation database in place. Because historical server arrival time was never stored, backfill `evaluated_utc` from `occurred_utc`, preserve every existing row, and create bounded lookup indexes. This backfill is compatibility evidence, not a claim that legacy client time became authoritative.

## Consequences

- Two service instances sharing one SQLite database cannot jointly exceed the same evaluated activity ceiling; an exact boundary race produces one qualification and one durable `rate_anomaly`.
- Backdated occurrence time does not bypass the new installation/entitlement velocity signals.
- The record remains pseudonymous and content-free. It adds one UTC server timestamp and no prompt, response, path, URL, artifact body, IP address or matched private value.
- Fixed ceilings are deterministic defense-in-depth, not proof of useful execution or fraud. Production still needs approved threshold ownership, identity/key provisioning and rotation, authoritative entitlements, cross-account analysis, appeal/false-positive handling, monitoring, retention/access policy and external acceptance.
- This decision grants no public receipt endpoint, deployment, publication, payment or payout authority.

## Alternatives rejected

- Trust only client occurrence time: a compromised client controls its bucket placement within the accepted time window.
- Return dimension-specific denial codes: that gives an attacker a threshold oracle without helping the current local simulation.
- Evaluate after insertion or outside the immediate transaction: concurrent service instances could observe inconsistent capacity.
- Delete or reinterpret legacy evidence during migration: arrival time cannot be reconstructed honestly, so exact rows are retained and the limitation is explicit.
