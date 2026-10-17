# P08 — Private local CKB/CCB

- Status: In progress
- Commands/results: none
- Changed files: none
- Commit: none

Required gate includes private isolation, offline retrieval, version conflicts and malicious-artifact rejection.

Active work: select and pin an embedded SQLite provider, implement physically separated per-profile metadata/vector/artifact stores, then validate version/dependency conflicts, hybrid offline retrieval, provenance and a sandboxed reusable-tool harness.
