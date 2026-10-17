# ADR 0027: path-private exact CCB artifact review and loopback intake

- Status: Accepted for local simulation
- Date: 2026-10-17

## Context

The P10 Windows surface could review and submit CKB metadata, while schema-v2 CCB packages were exercised only by test clients. A real contributor must be able to select one package, inspect every admitted textual component, approve the exact digest, and send those exact bytes without turning the contribution UI into a private-store browser or retaining a local path. Package replacement, an uncertain request, or an accepted lifecycle must not permit metadata/artifact substitution or a blind retry.

## Decision

Add an explicit CKB/CCB type choice and a CCB-only file picker to the native contribution window. Admit only one bounded, nonempty, regular, non-reparse schema-v2 ZIP. Open it without write/delete sharing, read an exact snapshot, validate the closed five-file profile, compute SHA-256, expose only the base file name and complete admitted review text, and immediately clear the entered full path. Never enumerate the private CCB store.

Bind resource ID, version, approved SPDX license and candidate artifact digest to the admitted manifest, SBOM, license text and package bytes. Scan the contribution body plus every admitted textual component and test input. Any candidate edit invalidates review and approval. Send the owned snapshot only with the separately approved, fixed literal-loopback intake action. Keep it in memory across an indeterminate result so the original origin can be reconciled without substituting bytes. Once intake or status confirms the item, zero the owned byte array, remove the review text from the UI, and lock candidate identity until a terminal state or `submission_missing`. If bytes were already cleared and the server reports missing, require selection, review, preparation and send approval again.

Do not add reviewer, approval, sandbox execution, remote/global origin or publication controls to the contributor client. Server quarantine remains the only successful intake state; authoritative CCB approval still requires the separately gated Linux worker and sandbox evidence.

## Consequences

- A keyboard/UIA-accessible contributor can review and submit an exact schema-v2 CCB package into local simulation quarantine without retaining or returning its source path.
- Snapshot hashing plus candidate digest binding prevents path mutation or metadata substitution between review and transport.
- Owned artifact bytes are zeroed on replacement, clearing, confirmed intake/status, withdrawal, window close and failed admission; immutable managed-string erasure is not claimed.
- The feature remains local simulation evidence. It does not authorize external transfer, execute community code on Windows, approve a CCB, or publish to the signed global catalog.
