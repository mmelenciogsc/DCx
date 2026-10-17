# ADR 0033: Register an already-installed Whisper backend by exact approved files

- Status: Accepted for fixture-backed registration; real backend approval remains blocked
- Date: 2026-10-17

## Context

The Whisper process adapter previously accepted hash-pinned configuration but had no accessible way for a user to identify an already-installed runtime and model, inspect both licenses, approve the exact files, retain the choice, or revoke it. Automatic acquisition would create install, provenance, licensing and resource authority that has not been approved. A friendly path alone would remain mutable.

## Decision

The local-model window registers four distinct, existing files on a local drive: a `.exe` Whisper runtime, a `.bin` or `.gguf` model, a runtime license and a model license. The UI exposes the complete UTF-8 text of both bounded license files plus SHA-256 and size evidence. Approval begins unchecked, is invalidated by any path change, and is required after review. Save reopens and rehashes all four files before atomically persisting their canonical paths, exact byte digests and approval UTC. Clear removes only the DCx binding.

Registration rejects network/device paths, reparse files or ancestors, unsupported types, empty/oversized files, malformed state and binary/control-filled licenses. Execution rechecks both approved license files before constructing the adapter. Every transcription then bounds and rehashes the executable and model, and holds the executable, model and signature-checked WAV under read-only/no-write/no-delete leases through child completion. The child has typed arguments, a scrubbed environment, bounded output, timeout/tree kill and private temporary output cleanup.

The UI provides unique keyboard access keys and stable UI Automation identifiers. It has no download or install action and explicitly states that digest review does not prove publisher authenticity, legal suitability or transcription accuracy.

The files/media viewer consumes only this persisted binding for direct admitted-WAV transcription. It pauses media, keeps a cancel action enabled during processing, retains no partial transcript on cancellation, and places successful bounded text in a read-only accessible transcript field. Invalid UTF-8 and unsupported control characters fail closed. Clearing the binding makes the consumer unavailable immediately. MP3/MP4 audio extraction is not authorized by this decision and remains explicitly unavailable.

## Consequences

- A user can review, approve, retain and revoke an exact already-installed backend without environment editing.
- Any executable, model or license-byte change invalidates the approved identity before use.
- Native UIA and a fake CLI prove consent, persistence and broker wiring without claiming speech-recognition quality.
- Native UIA also proves consumer cancellation, cleared-binding denial and transcript focus, but the fixed fake transcript remains broker evidence rather than accuracy evidence.
- P06 remains blocked until the owner authorizes real dependency acquisition or identifies an existing approved backend, and real speech accuracy, provenance, license, resource, cancellation and offline-performance gates pass.

## Alternatives rejected

- Automatic Whisper download/install: exceeds current authority and bypasses license/provenance review.
- Executable/model paths without license files: cannot bind the approved use to reviewable license evidence.
- Hashing normalized license display text: would not detect every exact file-byte change.
- Persisting complete license text: duplicates source material unnecessarily; exact digests and paths are sufficient to revalidate it.
- Treating the fake CLI transcript as model accuracy: it validates only the process contract.
