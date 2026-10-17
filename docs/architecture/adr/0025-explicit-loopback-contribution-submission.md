# ADR 0025: explicit literal-loopback contribution submission

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

P10 could prepare an exact reviewed contribution in the Windows UI and the separately gated simulation service could intake it, but only test code connected those boundaries. A native client path is needed to validate consent, accessibility and transport together without creating a general remote-upload capability or implying production publication authority.

## Decision

Permit the Windows UI to send only its in-memory exact prepared object, only after a second initially unchecked approval, and only to a root HTTP origin on a literal loopback IP address. Candidate, endpoint or token changes clear the relevant approval. Server acceptance must be `202`, match the submission identifier, report `Quarantined` and supply the matching status location; it never means reviewer approval or publication.

Use a hardened client with a fixed intake path, bounded connect/overall/body limits, no redirect, cookie, proxy or decompression behavior, exact response-origin checks and strict JSON with unknown-field rejection. Accept a bounded printable bearer token through a password control for one attempt, clear the control afterward, never persist or log it and zero the encoded envelope bytes after transport. Cancellation reports indeterminate acceptance and requires server reconciliation before retry.

Do not expose remote origins, artifacts, reviewer operations, withdrawal or publication through this intake client. ADR 0026 later adds minimized owner status and separately approved withdrawal on the same literal-loopback simulation boundary; it does not add reviewer or remote/global authority. Production contribution transport requires a new decision covering approved identity, TLS ingress, credential lifecycle, abuse controls, accessible review, privacy/legal policy and authoritative deployment.

## Consequences

- Native UIA automation proves the preparation approval and send approval are distinct, default off and reset after an accepted loopback quarantine submission.
- Unit and real-Kestrel tests cover literal-loopback admission, fixed transport behavior, strict response binding and stable failure codes.
- Ephemeral token values are checked against server diagnostics and retained validation data. The immutable managed token string may remain in process memory until garbage collection; the implementation makes no secure-memory-erasure claim.
- This is a local simulation submission boundary, not global upload, approval, publication or production identity. ADR 0026's owner lifecycle extension preserves those limits.
