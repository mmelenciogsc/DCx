# ADR 0100: Expose a zero-authority local pilot administration preview

- Status: Accepted for local fixture administration and incident dry-run evidence; deployed administration, enrollment and production activation remain blocked
- Date: 2026-10-19

## Context

P14 requires a pilot administration console and incident readiness. The existing readiness window exposed the shared `NO_GO` policy and local aggregate-consent controls, but it did not display retained aggregate rows or exercise the incident stop sequence. Calling that window a pilot administration console would have overstated its capabilities.

A locally testable administration boundary can still provide useful evidence without creating enrollment, event collection, metric transport, publishing, payment or activation authority. Persisted aggregate data must also be treated as untrusted same-user input when it is read back for display.

## Decision

- Add a clearly labeled **Local pilot administration preview** to the native readiness window. It reports that pilot enrollment, product event sources, network metric transports and production activation are unavailable.
- Display only cataloged aggregate buckets from the most recent 30 UTC days, plus the total bucket count. Never display raw events, prompts, outputs, URLs, paths, accounts or user identifiers.
- Revalidate every persisted row on read: exact catalog key, strict UTC day, defined and metric-permitted dimension, bounded sample count, valid minimum/maximum and a mathematically possible sum. Reject more than 10,000 rows rather than silently truncating a requested range.
- Define seven closed fixture scenarios for signing-key compromise, malicious global update, private-data exposure, inaccessible takeover, payout/reconciliation, updater rollback and backup/restore.
- The **Run local incident stop drill** action only builds an in-memory stop/preserve/fresh-readiness plan. It performs zero external actions, stores no exercise result and is available only while production activation is denied. The focused result states that enrollment, transport and activation remain unavailable.
- Keep the release decision and blocker policy read-only. Expose no control named or equivalent to **Enroll pilot**, **Send metrics** or **Activate production**.

## Validation

Implementation commit `4ff1a38` passes 15/15 P14 release tests, including hostile persisted-row denial and the seven-scenario zero-authority policy. Native UIA evidence at `artifacts/test-results/p14-0777d7de1de24fca855e5957b54934e9/` proves the boundary text, empty recent-aggregate view, focused dry-run result, six distinct access keys, zero enrollment/event/transport/activation controls, seven blockers and `NO_GO`. Full native CI passes 645/645. P13 evidence at `artifacts/test-results/p13-c0ce8c6ea1fd4fbf8c56cc7b308b4ee7/` packages and scans the implementation with zero unexpected secrets or NuGet advisories.

## Consequences and residual risk

- DCx now has a bounded local administration preview and deterministic incident stop drill; these close only the local fixture portions of the P14 admin/incident requirements.
- This is not a deployed or multi-user pilot administration service. It has no operator identity, enrollment roster, remote aggregate ingestion, support workflow, feature-flag mutation, external notification or incident closure authority.
- A dry-run plan is not a human tabletop or production incident exercise. Named on-call ownership, legally reviewed notification timelines, actual feature-flag containment and human JAWS/NVDA/Narrator acceptance remain blocked.
- Same-user database replacement or denial of service remains possible. Strict read admission prevents malformed rows from being represented as trusted metrics but does not provide hardware-backed integrity.
