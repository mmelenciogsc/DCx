# ADR 0051: Admit and approve local-model benchmarks through an accessible exact-review surface

- Status: Accepted for local fixture-backed review and execution; real Whisper/embedding acceptance remains blocked
- Date: 2026-10-18

## Context

ADRs 0049 and 0050 provide bounded aggregate scoring and an exact short-lived approval contract, but a boolean supplied by a caller is not a user review workflow. A real run needs a keyboard- and UIA-accessible surface that presents the backend, fixture-suite and policy identity the user is approving, revalidates every input after approval, supports cancellation and persists no fixture content.

Fixture manifests are untrusted local input. Ambiguous JSON, path traversal, reparse points, duplicate fixture weighting, mutable files or an unavailable backend must not turn into a run.

## Decision

DCx admits schema-v1 transcription and embedding-retrieval manifests of at most 512 KiB. JSON comments, trailing commas, duplicate properties, unknown properties, unsupported versions/kinds and missing or out-of-range values fail closed. Each manifest includes bounded name, source and license fields plus exact evaluation/resource thresholds.

Transcription WAV names are unique relative paths beneath the manifest directory. Absolute, network, alternate-stream and escaping paths are rejected. The manifest, WAVs and their directory chains must be existing regular local non-reparse inputs. Suite preparation then applies ADR 0050's exact leased byte/length/text binding. Embedding documents, queries and relevance labels use the same runner bounds and digest contract, but cannot execute until an approved production embedding adapter with real resource probes exists.

The native review window exposes stable UIA identities, assertive status, read-only review/result fields and distinct access keys. It displays manifest, backend, suite and policy SHA-256 values; provenance/license; fixture counts; quality thresholds; and resource limits. Approval is disabled until a compatible approved backend exists and a strict review succeeds, starts unchecked, and authorizes one run for 20 minutes within ADR 0050's 30-minute maximum.

Execution reloads the manifest, recomputes manifest/suite/policy digests, reloads the approved Whisper binding and compares exact identity before invoking the production adapter. A path edit, completion, failure or cancellation revokes approval. Cancellation retains no partial result. Passing or failing completion atomically replaces only the latest per-user aggregate evidence; it never changes a production model/backend selection.

The evidence store retains only ADR 0050's schema-v1 aggregate evidence. It rejects reparse directory chains and unsafe existing targets, uses same-directory write-through temporary files and atomic replacement, and keeps no manifest path, fixture identifier, expected/generated transcript, corpus/query/relevance text or vector.

## Consequences

- The same exact digests shown for review are re-established immediately before and during execution.
- Missing backends and the not-yet-approved embedding adapter are explicit unavailable states, never invented zero results.
- Native UIA fixtures prove default-off approval, exact review, production-adapter execution, result focus, path-change revocation, cancellation, prior-evidence preservation and missing-backend denial.
- Strict manifest handling and aggregate evidence have unit coverage for duplicate/unknown JSON, traversal, duplicate WAV weighting, mutation and privacy shape.
- Generated fixture success is not real speech accuracy, publisher authenticity, license approval or production acceptance. No runtime/model was installed or downloaded.
- Human JAWS, NVDA and Narrator review and real approved Whisper/embedding quality/resource runs remain phase blockers.

## Alternatives rejected

- Let users type thresholds directly beside an unbound fixture directory: review and execution could diverge.
- Persist manifests or raw per-fixture outputs as evidence: unnecessary exposure of local content and paths.
- Enable Run while approval is unchecked or the backend is absent: creates ambiguous consent and fabricated availability.
- Automatically promote a passing backend to production: benchmark approval is deliberately separate from production selection.
