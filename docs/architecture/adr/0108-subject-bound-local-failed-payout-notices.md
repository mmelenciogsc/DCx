# ADR 0108: Project failed-payout decisions as subject-bound local notices

- Status: Accepted for controlled local validation only
- Date: 2026-10-19

## Context

ADR 0107 proved that an operator could review one durable failed-payout hold and append a zero-authority decision through the authenticated local boundary. The `request_recipient_update` result said that a local dashboard notice was ready, but the contributor dashboard did not derive that notice from authoritative state. Treating the operator result alone as contributor delivery would have been false: it contained no authenticated contributor projection and no notification transport.

## Decision

- Extend the provider-neutral contributor dashboard with at most 120 read-only local failed-payout notices. Each contains only a domain-separated opaque SHA-256 notice ID, one already-authorized CARE period, integer-cent amount, closed notice code and UTC record time.
- Derive notices under the repository mutation lock and the same SQLite read transaction as the contributor's period projection. Join the failed item and opaque hold to its exact payout-allocation binding, require the authenticated contributor pseudonym, require an unsuccessful source item and require the period to be in that contributor's bounded projection.
- Verify the complete append-only failed-payout disposition chain before projecting a notice. The selected disposition's occurrence, closed code and record digest must exactly match the verified chain. Unsupported decisions or any mismatch fail closed.
- Never expose the internal operator review ID. Derive the notice ID from a domain separator, the random internal review ID and the chained record digest. The notice is therefore stable for exact replay without becoming an operator capability or disclosing the review reference.
- Bind each notice to a known period, a positive amount no greater than that period's allocation, one of three closed codes, a nonfuture UTC time, unique ID and deterministic descending order. An unavailable dashboard must contain neither periods nor notices.
- Keep the contributor endpoint read-only and add `Vary: Authorization` to its existing non-cacheable response. The native dashboard presents a separate keyboard/UIA list and states explicitly that no external message was sent, no automatic retry occurs and notification transports remain zero.
- Do not add an email, push, webhook, acknowledgment, contact-address lookup, payout retry/release action or provider call. A local notice is not evidence of external delivery.

## Validation

Implementation `8ee6370` passes the locked native Windows Release build with zero warnings/errors, 138/138 finance tests and the full 663/663 suite. Exact clean-source P11 evidence is `artifacts/test-results/p11-52464659d2984fd09cc2a9b52a7d3af0/`.

The generated native UIA path presents one privacy-minimized notice. The real child-Kestrel/SQLite operator path appends `request_recipient_update`, then the separately authenticated contributor role reads exactly one `recipient_update_requested` notice for the bound subject. The response excludes the operator review reference, batch, snapshot, recipient address, operator digest and other contributor identities. Restart persistence and audit-chain tamper denial pass. Provider calls, financial mutations, payout attempts, notification transports and production actions remain zero.

## Consequences and residual risk

- The previous claim that a local notice was ready now has an authoritative contributor-facing projection, without creating messaging or payout authority.
- Notice ID, period, amount, code and time are linkable sensitive financial state. They are not anonymous and require an approved production retention, access, export, correction, deletion and contributor-notice policy.
- The static subject token, literal-loopback HTTP, same-machine database and UIA fixture are not production identity, MFA, TLS, multi-user authorization, secret rotation/revocation, distributed rate limiting or malicious same-user isolation.
- External contributor delivery/acknowledgment, production identity and deployment, credentialed PayPal sandbox acceptance and human JAWS/NVDA/Narrator testing remain blocked.
