# ADR 0039: Explicit failed payout item remediation

- Status: Accepted for durable uncredentialed sandbox validation; operator/provider acceptance blocked
- Date: 2026-10-17

## Context

A terminal PayPal Payouts batch can fail in whole or in part. Automatically copying every failed item into another batch risks duplicate payment, repeating a bad destination, bypassing owner review, or accidentally resending an item that already succeeded. Crash recovery for an already approved batch is different from authorizing a new remediation payout and must retain the original provider-safe batch identity.

## Decision

DCx creates no remediation batch automatically. A review may select only currently unsuccessful items from one terminal `failed` or `partially_succeeded` source batch. Contributor pseudonym, amount and Net 60 eligibility date remain exact; every replacement recipient snapshot ID and destination digest is reviewed as part of a distinct target preview. Successful source items are structurally ineligible.

The review binds the complete source result digest, selected source ordinals and statuses, exact replacement preview, UTC review time and ten-minute expiry into one SHA-256. Reservation requires separate explicit owner approval, current sandbox account eligibility and the normal date gate. Schema v7 atomically writes the reserved target batch, its authorization marker and immutable source-to-target item lineage. A source item can have only one remediation, and a target item can have only one source. Missing, changed or deleted lineage blocks completion and restart reconciliation.

Only after reservation may the sandbox provider receive the target preview. A provider/process failure leaves that exact approved batch reserved for the existing lookup-first, stable-ID crash recovery; it does not create a second remediation. A terminal target posts only its successful amount through the existing balanced journal.

## Consequences

- Remediation cannot include a successful item, change a failed amount, reuse the source batch ID, bypass expiry/approval/eligibility, or create two remediation branches for one failed item.
- Durable lineage contains pseudonyms, cents, provider status, batch/ordinal IDs, recipient snapshot IDs, destination/result/review digests and UTC approval time. It contains no plaintext email or provider credential.
- Handler-backed tests prove denial, tamper detection, restart recovery, schema migration and balanced accounting without a PayPal request.
- No accessible production operator surface, contributor notification/hold policy, credentialed provider acceptance or production schedule exists. Those remain blockers; this ADR does not authorize a real payout.
