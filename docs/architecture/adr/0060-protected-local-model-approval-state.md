# ADR 0060: Protect local-model approval state from filesystem replay

- Status: Accepted for per-user filesystem-replacement defense
- Date: 2026-10-18

## Context

DCx persists explicit approvals for digest-qualified Ollama vision/embedding models, an exact Whisper executable/model/license set, and an exact FFmpeg executable/license/signature review. ADR 0054 confines those files to revalidated non-reparse storage, and every consumer rechecks the selected files or runtime identity. Confinement alone cannot distinguish the current approval file from an older, structurally valid file replayed by an actor with filesystem access. Treating an unanchored legacy approval as current would also silently convert existing bytes into renewed consent.

## Decision

Bind every nonempty local-model approval state to a separate current-user Windows Credential Manager fact. The protected fact contains only a schema version, closed state kind, SHA-256-derived storage identity, exact state byte length and SHA-256. It contains no model path, account identifier, prompt, media or secret.

The stores hash the exact serialized bytes after atomic filesystem replacement and update the protected fact before reporting success. Reads strictly validate the state first, then require an exact protected length and digest. Missing, malformed, unreadable or mismatched protection makes all approvals in that state unavailable and sets an explicit `RequiresReapproval` condition. It never automatically anchors legacy or changed state. The accessible local-model manager announces that the affected selection requires review and explicit approval again.

A fresh exact review and approval may replace rejected state. Clearing all approvals removes the protected fact before publishing the empty state, so a failed deletion cannot publish a misleading clear and a later failure cannot leave old state usable. An unanchored state containing no approval is safe. If a filesystem write succeeds but protected-state update fails or is canceled, the new approval remains unusable until the user repeats review and approval. DCx's existing exclusive per-data-root application lease serializes normal product mutations; all consumers reread the protected state rather than caching authority across operations.

## Consequences

- Filesystem-only deletion, modification and rollback of valid Ollama, Whisper or FFmpeg approval JSON cannot create usable approval authority.
- Existing nonempty state from before this decision is deliberately unavailable until explicit reapproval. There is no trust-on-first-use migration.
- Clearing remains conservative and recoverable and does not delete any runtime, model, executable or license file.
- Unit tests cover rollback, missing/unreadable anchors, protected-write failure, clear and reapproval. Native Windows tests exercise Credential Manager overwrite, isolation, deletion, cancellation and strict malformed-state denial. UI Automation proves screen-reader-visible Whisper and FFmpeg reapproval warnings.
- Credential Manager protects against filesystem-only replacement. It is not a boundary against arbitrary code already executing as the same Windows user, and it does not establish publisher authenticity, legal approval, model quality or permission to download, install, execute, publish or transmit data.
