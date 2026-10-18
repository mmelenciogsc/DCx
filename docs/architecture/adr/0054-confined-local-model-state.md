# ADR 0054: Confine local-model state to revalidated non-reparse storage

- Status: Accepted for storage confinement; approval-replay limit superseded by 0060
- Date: 2026-10-18

## Context

DCx persists approved Ollama, Whisper and FFmpeg identities plus aggregate benchmark evidence below the per-user application-data root. The individual state files rejected direct reparse points, and benchmark evidence checked its directory ancestors, but the three selection stores did not reject a linked ancestor. A settings or evidence directory could also be replaced by a link after a store opened. That could redirect approval state or aggregate evidence outside the intended per-user tree.

## Decision

Use one local-model storage guard for all four stores. Application-data, settings and evidence directories must be fully qualified local, non-root, existing regular directories whose entire existing ancestor chain is non-reparse. State and evidence targets must have a revalidated safe parent and must be regular non-reparse files when present.

Revalidate the directory, destination and newly written temporary file immediately before atomic replacement. Cleanup must first revalidate the directory and must abandon cleanup instead of following an unsafe replacement. Open, read, set, clear and evidence-save operations fail closed after a directory or target substitution.

## Consequences

- Linked application roots, linked state/evidence files and settings/evidence directories swapped after open are denied without writing through the link.
- Ollama, Whisper and FFmpeg approvals retain their existing schema and accessible consent behavior.
- The latest aggregate benchmark evidence retains its content-minimized schema and atomic replacement behavior.
- Filesystem checks narrow path redirection but cannot eliminate a same-user check/open race. Production ACLs and process isolation remain required; an unsafe cleanup path can leave an inert temporary file in the detached original directory rather than risk deleting through a replacement.
- ADR 0060 separately protects nonempty approval-state byte identity in Windows Credential Manager. This ADR continues to govern filesystem confinement.
