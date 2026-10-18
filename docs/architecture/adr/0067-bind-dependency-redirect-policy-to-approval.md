# ADR 0067: Bind dependency redirect policy to approval and protected receipt

- Status: Accepted for approval/receipt binding; route schema and source are superseded by ADR 0072
- Date: 2026-10-18

## Context

ADR 0066 introduced a deny-by-default bounded redirect mechanism. The transfer plan and item approvals authenticated payload identity, but did not identify the redirect-policy revision the user reviewed. A future policy change between accessible preflight and transfer could therefore expand network authority without changing the plan digest. A completed-set receipt also lacked the policy identity needed to explain the historical transfer boundary.

## Decision

Represent redirect authority as a canonical review. ADR 0072 advances the current review to schema v2 with at most 32 transitions, sorted by an ordinal identity that binds the plan-item source URI, expected bytes, content SHA-256 and exact query-free previous/target HTTPS URIs. A domain-separated SHA-256 binds the canonical route sequence. Empty routes produce the startup deny-all review. Duplicate, malformed, noncanonical or digest-substituted reviews fail closed.

Item and set approvals use schema v2 and bind both the plan digest and reviewed redirect-policy digest. The desktop displays the digest and every route in a keyboard-readable preflight, retains that displayed snapshot, and names the redirect-policy authority in the unchecked one-time approval. It never derives approval from a later policy read.

The HTTP transport authorizes redirects from the approved canonical review data, not from unreviewed implementation behavior. It compares the approval to the current policy before the first request, rechecks the current digest after every approved redirect and again after payload authentication, and rolls output back if the policy changes. The set coordinator snapshots the approved policy, checks it before and after each item, requires item results to report the same digest, and places that digest in the schema-v2 receipt. The existing Credential Manager anchor protects the complete serialized receipt, including this digest, before atomic publication.

Later read-only verification authenticates the protected historical receipt and its syntactically valid policy digest; it does not require the current policy to equal an older completed set. A policy update cannot rewrite history and an old receipt cannot authorize a new transfer.

## Consequences

- Enabling a future provider route requires a visibly different digest and new one-time approval.
- Policy substitution before network access is denied; a mid-operation policy change removes partial output and prevents publication.
- Completed sets retain privacy-safe policy provenance without storing signed redirect query values, local paths, credentials or account data.
- Synthetic tests cover deterministic ordering, malformed review, stale approval, hostile behavior, mid-transfer mutation, receipt tamper and accessible review. They make no provider request.
- Startup and any missing/expired review remain deny-all. ADR 0072 permits only explicitly initiated HEAD observation; payload transfer still requires separate exact approval.
