# ADR 0041: Fixture-only accessible CARE operations review

- Status: Accepted for native generated-data validation; authenticated production operations blocked
- Date: 2026-10-18

## Context

Schema v8 provides durable verified-event quarantine and identifier-free aggregate health, but DCx had no native keyboard/screen-reader surface for reviewing that state or rehearsing a bounded local cycle. Connecting the Windows client directly to the server database would violate the client/server boundary. Inventing an unauthenticated admin API, displaying provider identifiers, or treating generated UI success as production operator acceptance would create new security and accessibility risks.

## Decision

Define a provider-neutral CARE operations contract in Core. Health contains only stable status, aggregate state counts, maximum attempt count, oldest/next UTC times and a processing-capability flag. Closed validation rejects inconsistent counts, times, state priority and implied authority. A processing result is limited to 100 scanned records, must reconcile its outcome counts exactly and must report zero provider calls. The processing request carries the exact reviewed health snapshot plus separate explicit approval so a source can reject stale review.

The normal Windows application uses an unavailable source. Its CARE operations window says that no connection is configured, does not infer an empty or healthy queue, and cannot process. A deterministic source is enabled only by the explicit validation environment flag. It uses generated aggregate values, has no credential or transport, allows one bounded cycle, verifies the unchanged reviewed snapshot, and then disables replay.

The window exposes a focusable heading, read-only aggregate summary, assertive status, three distinct keyboard access keys and an initially unchecked approval. Denial returns focus to approval. Completion moves focus to the updated summary and announces exact aggregate outcomes and zero provider calls. Event, provider, plan, subscription, sale, account, recipient and contributor identifiers are absent.

## Consequences

- Automated native UIA can validate keyboard, focus, approval and announcement behavior without PayPal credentials, data or calls.
- This does not authenticate an operator, connect to the Ubuntu service, schedule work, notify contributors, dispose blocked events or authorize a payment/payout.
- A production source requires a separate approved authenticated admin transport, authorization/role policy, audit/retention policy, server-side stale-review enforcement, scheduler/alerting and human JAWS/NVDA/Narrator acceptance.
- ADR 0042 adds server-side stale-review enforcement for one local repository instance without changing the normal desktop source or satisfying authenticated/distributed production operations.
