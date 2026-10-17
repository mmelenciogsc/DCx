# ADR 0026: minimized owner contribution lifecycle

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

ADR 0025 connected exact-item preparation to literal-loopback simulation intake, but a canceled or malformed response could leave acceptance uncertain. The user could not reconcile that state or exercise P10's owner withdrawal/deprecation requirement through the accessible native surface. Retrying intake blindly or returning the full reviewed body from an owner-status endpoint would create avoidable replay and privacy risk.

## Decision

Add owner status and withdrawal to the same fixed, literal-loopback-only client. Give submit, status and withdrawal distinct initially unchecked approvals. Bind the prepared submission to its first attempted origin, disable duplicate send while it may exist, and require a minimized exact-digest status query before retry or withdrawal. Keep withdrawal disabled until a strict response for the exact submission and candidate digest reports `Approved`.

Return owner status as submission ID, candidate digest, state, UTC lifecycle times and optional release sequence only. Do not return the candidate, body, findings or contributor/reviewer identities to the contributor client. Return withdrawal as the exact submission ID, candidate digest, `Withdrawn` state and bounded release descriptor. Require strict JSON, state-field invariants and exact response origin for both.

Treat cancellation or an invalid response as indeterminate. Clear the known state and require reconciliation rather than automatic retry. Lock candidate, origin and credential inputs while a request is running; clear the password control and action approval after every attempt. Zero encoded metadata, request/envelope/artifact buffers and bounded response buffers where managed byte ownership permits; do not claim erasure of immutable managed strings.

## Consequences

- The native simulation surface now covers preparation, quarantine intake, minimized owner reconciliation and separately approved withdrawal/deprecation without exposing reviewer approval or remote/global endpoints.
- Cross-origin lifecycle actions fail locally, and an accepted or uncertain intake disables another send until the original bound origin reports the item missing.
- Reviewer approval remains a separate server-side role and is never exposed by the contributor UI.
- This remains controlled local simulation evidence, not production identity, publication authority, remote transfer or secure-memory erasure.
