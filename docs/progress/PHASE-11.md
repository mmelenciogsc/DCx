# P11 — PayPal subscriptions and CARE

- Status: BLOCKED (verified-event mapping, durable causal quarantine, accounting and failed-item remediation lineage pass; credentialed PayPal sandbox acceptance, eligibility, product/plan provisioning and production operator/policy gates remain unavailable)
- Implementation/validation commits: `2f97540`, `3984c6b`, `4b05cc0`, `dc25a87`, `c021520`, `df1c0b8`, `9877a60`, `faf6b71`, `1631ebb`, `599cee9`, `ec37d1f`, `a662abb`, `8583b29`, `d5624a6`, `9576d63`, `261f6ee`, `76d0750`
- Documentation commit: recorded by the commit containing this file

No PayPal credential was accessed, no PayPal API request was made, and no subscription, payment or payout was attempted. The only PayPal network activity was six bounded read-only GET requests to official developer documentation/schema URLs.

## Implemented

- US$3 monthly subscription event fixture with signed deterministic webhook mock, strict transmission/event replay semantics, bounded identifiers and exact event/amount/currency/time validation.
- Refund/reversal journal and entitlement suspension; checked integer-cent double-entry ledger with balanced-entry validation and reconciliation.
- SQLite authoritative mock ledger using WAL and `synchronous=FULL`, immutable canonical event hashes, durable postings, minimized decisions, entitlements and payout state.
- Shared causal entitlement reducer: newer signed events apply, stale events cannot overwrite current state, and same-time deactivation dominates activation.
- Positive distributable-net-profit formula and deterministic allocation capped at 4,000 basis points (40% aggregate), with exact cent conservation, threshold holds and Net 60 eligibility.
- Sandbox-only payout coordinator requiring account eligibility, explicit owner approval and Net 60; `Live` is structurally disabled.
- Restart-safe payout reservation/reconciliation with digest-bound previews, stable local batch IDs and durable terminal state.
- Official-contract-shaped PayPal sandbox adapter for OAuth client credentials, webhook-signature verification, payout submission and payout lookup. Its origin is fixed to `https://api-m.sandbox.paypal.com`; redirects, cookies, proxies and decompression are disabled; request/response sizes, time, JSON depth and schemas are bounded.
- Credential leases own byte arrays and zero them on disposal. Request/response byte buffers are zeroed; provider bodies, credential values and transport exception text never enter stable errors. Transient .NET `string` values used for Basic/Bearer headers cannot be securely erased and are documented as a residual limitation.
- Webhook verification requires the exact official evidence fields, an allowlisted PayPal certificate URL and a duplicate-property-free event object. Only official `SUCCESS`/`FAILURE` verification states are admitted.
- The same immutable parsed event object is both sent for official verification and mapped after verification, closing caller-field substitution and post-verification mutation gaps. A verification `FAILURE` cannot produce a ledger event.
- Current documented subscription lifecycle events require exact resource type, status, plan ID, subscription ID and RFC 3339 occurrence time. Activation binds the provider subscription to the one configured plan; payment cannot create that binding and fails closed until activation has established it.
- A completed US$3 sale binds its provider sale ID to that subscription. Refunds/reversals must resolve through the durable sale binding, cannot substitute a subscription, and cannot cumulatively exceed recorded sale or subscription receipts. Rejected corrections roll back their identifiers and mappings atomically.
- Each owner-approved payout item now binds a unique immutable recipient-snapshot ID and SHA-256 of the exact destination. At send time, the verified/eligible sandbox directory record must match snapshot ID, contributor and destination digest before credentials are acquired. The durable preview stores no plaintext email address.
- The native CARE recipient window reviews the exact contributor, email and digest; input edits invalidate review. Save and revocation have separate initially unchecked approvals and six distinct keyboard-focusable access keys. The exact email is cleared from the UI after save.
- The current-user Windows Credential Manager contains the exact destination and complete binding under an opaque random target. The bounded atomic JSON index contains only pseudonym, digest, target, lifecycle and UTC audit metadata. Pending-write recovery removes orphaned credentials; protected/index mismatch, duplicate active contributor/destination, malformed schema and reparse paths fail closed. Revocation makes the binding unusable before deleting the secret.
- Owner approval is not provider verification: the concrete directory always returns `IsVerifiedAndEligible = false`. The UI and store have no PayPal credential, network or payout action.
- Payout submission requires unique syntactically valid email addresses, exact USD amounts and bounded batch totals. Stable `sender_batch_id`, sender item IDs and a hashed `PayPal-Request-Id` make retries deterministic. Lookup also verifies each returned receiver against the approved destination digest.
- A schema-v8 repository stores subscription-to-plan, sale-to-subscription, correction-object and cumulative financial bindings in addition to the unique local-to-provider payout reference, result digest, paid total, exact per-item outcomes, immutable later-return adjustments, failed-item remediation lineage and verified-event dependency quarantine. A crash after PayPal acceptance but before local return reconciles through the provider reference instead of resending.
- Only an officially verified, strictly mapped sale/refund/reversal whose subscription or sale binding has not arrived may enter quarantine. The durable record contains bounded normalized fields and digests, never the raw webhook, signature, credential or provider response. Exact replay is inert; event/transmission reuse with different content and collisions from the fixture path fail closed.
- The explicit local reprocessor performs no provider request, scans at most 100 due records, prioritizes sales before corrections, and backs unresolved dependencies off from two seconds through one hour. Apply/completion commits atomically; retry/blocked state is written only after rolling back failed mapping work. Nonremediable state becomes blocked, and stored-record tamper stops without ledger mutation.
- A read-only aggregate health snapshot exposes only stable status, pending/due/blocked/completed counts, maximum retry depth and oldest/next UTC times. It has no event, transmission, provider, plan, subscription, sale, account or contributor identifier. `operator_action_required` is explicit when any record is blocked; this is alert-ready state, not a scheduler, notification or disposition workflow.
- `PENDING`, `PROCESSING` and pending/unclaimed/on-hold item states remain reserved and never post cash. A completed mixed result posts only successful item amounts, retains failed amounts in contributor payable, persists item status, and becomes `partially_succeeded`; no remediation batch is created automatically.
- A ten-minute review can select only unsuccessful items from one terminal source batch. It binds the source result, exact source ordinals/statuses, unchanged contributor/amount/Net 60 date and every replacement snapshot/destination digest into a review SHA-256. Separate owner approval and sandbox eligibility are required before atomic target reservation.
- Schema-v8 retains the v7 authorization markers and one-to-one source/target lineage that prohibit successful-item inclusion, amount/date substitution and multiple remediation branches for one failed item. Missing lineage blocks completion and lookup-first restart recovery. A process/provider failure can resume only the same already-approved stable target batch.
- A later provider lookup can move a previously successful item once to `RETURNED`, `REFUNDED` or `REVERSED`. The path never sends/resends, denies promotion or status rewriting, records an immutable adjustment, and atomically restores cash and contributor payable with a balanced correcting journal. Exact replay is a no-op; missing provider state and conflicts fail closed.
- Schema-v3 reserved batches lack recipient bindings and migrate to `review_required` rather than being resent. Schema-v8 tables are additive; unknown future CARE schema versions fail closed. Version 1→2→3→4→5→6→7→8 migration and reopen behavior are tested.

## Reproduction

```bash
cd /mnt/d/DCx
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\validate-p11.ps1
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File D:\DCx\scripts\ci.ps1
```

The separate official-contract audit is read-only and must not be confused with a PayPal API test:

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\audit-paypal-contract.ps1
```

## Measured result

- Locked Release build: 0 warnings, 0 errors.
- P11 exact-namespace tests: passed 62, failed 0, skipped 0; about 1 second.
- P11 evidence: `artifacts/test-results/p11-cc3bc094633b4ef78f9e5f56c3d9b618/` with `p11-finance.trx` and `recipient-snapshot-uia.json` (generated from the source committed through `76d0750` and intentionally not committed).
- Full native suite: passed 333, failed 0, skipped 0; about 8 seconds in the recorded post-commit run.
- Official-contract evidence: `artifacts/test-results/paypal-contract-c2619695def04e9c9302b09f203d5538/paypal-contract-audit.json`; official sources 6, credentials used 0, PayPal API requests 0, subscription/payment/payout attempts 0.
- Adapter tests cover exact request shapes, sandbox-origin pinning, redirect/origin denial, bounded JSON, duplicate properties, credential/provider-text non-disclosure, recipient-snapshot substitution before credential use, idempotent retry, durable reference recovery, pending state, complete success, receiver/amount mismatch and mixed item outcomes.
- Repository tests prove a 500-cent success plus 700-cent failure posts only 500 cents to cash, leaves 700 cents payable, survives reopen with exact item states, accepts exact replay and denies a conflicting terminal result.
- Post-terminal tests prove an initially successful 500-cent and 700-cent batch can later record a 500-cent return and then a 700-cent reversal exactly once, restoring cash from -1,200 to 0 and contributor payable from 0 to -1,200 while remaining balanced across reopen. Promotion, rewrite, missing-provider and resend paths are denied; provider send calls remain zero.
- Five remediation tests prove schema-v6→v7 migration, successful-item exclusion, exact one-time failed-item lineage, separate owner/eligibility gates, expiry and digest denial, balanced payout after remediation, provider-failure restart recovery and fail-closed behavior when authorization lineage is deleted. Fixtures made no PayPal request.
- Eight recipient-store tests cover default-off approval, exact protected binding, plaintext exclusion, restart, duplicate prevention, tamper denial, revocation, canceled/failed writes, orphan recovery and malformed identities. Native UIA proves review invalidation, default-off save/revoke, distinct access keys, Credential Manager reopen, cleared input, minimized list/state, false provider eligibility, deletion on revoke, network requests 0 and payout attempts 0.
- Verified-event tests prove activation→US$3 sale→partial refund across repository reopen, deny wrong-plan activation, malformed/unverified events and excessive corrections, then accept a corrected reuse of an identifier whose rejected transaction was rolled back.
- Five quarantine tests prove payment/refund-before-dependency survival across repository reopen, sale-before-correction causal replay, bounded backoff, zero provider calls during reprocessing, exact replay, cross-path/event-content conflict denial, nonremediable blocking, digest-tamper rejection without an audit or ledger mutation, and an exact aggregate-only health contract with identifier fields structurally absent.
- Payment, refund, entitlement, balanced-entry and payout state survive repository reopen. Conflicting replay IDs fail closed, and a rejected invalid event does not consume identifiers.

## Changed files in the latest milestone

- Official sandbox boundary and verified mapping: `src/DCx.Core/Finance/PayPalSandboxHttpAdapter.cs`; fixture policy and correction cap: `src/DCx.Core/Finance/PayPalSandboxWebhook.cs`.
- Pending-aware payout contract and coordinator: `src/DCx.Core/Finance/PayoutSafety.cs`.
- Durable schema-v8 plan/sale/correction, recipient/result binding, partial posting, later-return adjustment, remediation authorization, verified-event quarantine and migrations: `server/DCx.GlobalService/DurableCareRepository.cs`.
- Bounded local quarantine reprocessing, backoff, digest verification, atomic completion and minimized health snapshot: `server/DCx.GlobalService/OfficialWebhookQuarantine.cs`, `docs/architecture/adr/0040-durable-verified-webhook-dependency-quarantine.md`.
- Expiring failed-item review, immutable lineage, coordinator and denial checks: `server/DCx.GlobalService/FailedPayoutRemediation.cs`, `docs/architecture/adr/0039-explicit-failed-payout-item-remediation.md`.
- Compensating payout-return journal: `src/DCx.Core/Finance/CareLedgerJournal.cs`; one-way policy: `docs/architecture/adr/0037-idempotent-post-terminal-payout-returns.md`.
- Protected owner snapshot store and policy: `src/DCx.Core/Finance/PayoutRecipientSnapshotStore.cs`, `docs/architecture/adr/0038-protected-owner-approved-recipient-snapshots.md`.
- Accessible workflow and native gate: `src/DCx.Desktop/CareRecipientsWindow.xaml`, `src/DCx.Desktop/CareRecipientsWindow.xaml.cs`, `scripts/validate-p11-recipient-ui.ps1`.
- Adapter, causality, replay, migration and denial coverage: `tests/DCx.Core.Tests/Finance/PayPalSandboxHttpAdapterTests.cs`, `tests/DCx.Core.Tests/Finance/DurableCareRepositoryTests.cs`.
- Exact-namespace and official-reference gates: `scripts/validate-p11.ps1`, `scripts/audit-paypal-contract.ps1`.
- Contract record: `docs/finance/PAYPAL_SANDBOX_CONTRACT.md`, `docs/architecture/adr/0035-fail-closed-paypal-sandbox-boundary.md`, `docs/architecture/adr/0036-verified-paypal-subscription-bindings.md`.

## Blockers and limits

- No PayPal sandbox client ID/secret, webhook ID, Business/Payouts eligibility evidence or provider-verified recipient directory is available. The adapter is intentionally unconfigured and unwired; official sandbox acceptance has not occurred.
- Verified provider-event-to-local-subscription mapping and a durable local out-of-order dependency queue now exist, but no official product/plan creation or retrieval adapter, approved exact sandbox product/plan ID, credentialed delivery or provider acceptance evidence exists. The queue has no production scheduler, monitoring/alerting or accessible operator disposition surface.
- A protected local owner-approval directory and automated keyboard/UIA gate now exist, but provider verification is structurally false. Production retention/subject-rights policy, recipient notice, eligibility evidence and human JAWS/NVDA/Narrator acceptance remain absent. Metadata and protected recipient credentials are intentionally outside the current backup roots.
- Initial mixed outcomes are accounted item by item without automatic retry. The durable separately approved remediation boundary exists, but no accessible operator surface, approved hold/recipient-notification policy or deployed workflow exists. Later-return/remediation reconciliation is callable and tested, but production scheduling and credentialed provider acceptance remain absent.
- Profit definition, reserves, cost attribution, thresholds, held balances, post-close adjustments, tax reporting, contributor classification, privacy retention and regional payout rules require accounting/legal approval.
- Unblock only after explicit approval to provision sandbox credentials, configure and validate the exact US$3 sandbox product/plan, evidence merchant/Payouts and recipient eligibility, approve a provider-verification/retention policy for protected snapshots, and wire production quarantine/reconciliation scheduling, alerts, accessible failed-item/quarantine disposition and contributor-notification workflows; then run authorized sandbox happy/failure/out-of-order/refund/chargeback/duplicate/crash-window/remediation/later-return acceptance. Live mode remains disabled pending separate production approval.
