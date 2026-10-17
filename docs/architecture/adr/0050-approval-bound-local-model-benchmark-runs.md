# ADR 0050: Bind local-model benchmark execution to exact approved inputs

- Status: Accepted for core execution; accessible review is governed by ADR 0051 and real-backend acceptance remains blocked
- Date: 2026-10-18

## Context

ADR 0049 makes transcription and retrieval scoring bounded and aggregate-only, but a scorer alone cannot prove that observations came from the fixture bytes, backend identity and thresholds the user reviewed. Substituting a WAV, corpus text, model/runtime file, license or policy between review and execution would make otherwise valid metrics misleading.

## Decision

Every run requires explicit approval valid for no more than 30 minutes. Approval binds three domain-separated SHA-256 values: an exact backend binding, fixture suite and evaluation policy. Freshness uses DCx's UTC clock; public callers cannot supply an earlier time.

Backend bindings hash 1–8 uniquely labeled exact identity digests in ordinal label order. The Whisper adapter binds executable, model and both license-file digests. It recreates the approved transcriber for each fixture, revalidating both licenses before the executable/model are rehashed and executed. A completed child process is the unload boundary.

Transcription suite preparation orders unique fixture IDs and hashes expected text, exact WAV length and exact WAV bytes, not file paths. Execution reopens all approved absolute local WAVs under read-only/no-write/no-delete leases, recomputes the suite digest, checks backend identity before and after every call, runs sequentially and releases every lease on completion, denial or cancellation.

Embedding suites bind ordered document/query IDs and text plus ordered relevance labels. Execution recomputes the suite and policy digests, checks backend identity around each call and batches at most 32 inputs sequentially. Resource observations are summed for elapsed time, use peak working-set/GPU values and require every batch to confirm unload. The core accepts an injected embedding backend because no approved real Ollama embedding model/resource probe exists yet.

Evidence schema v1 contains only the approval/backend/suite/policy digests, DCx capture time and ADR 0049's aggregate scores/resources/failure codes. It contains no fixture path, transcript, expected text, corpus/query text, identifier, relevance label or vector.

## Consequences

- Exact review and exact execution are cryptographically linked without persisting benchmark content.
- Fixture mutation, policy substitution, missing/expired approval, backend identity change, malformed output, missing unload and cancellation fail closed.
- Generated tests prove file locking, cancellation cleanup, deterministic batching, digest stability, privacy shape and composition through the pinned fake Whisper process.
- ADR 0051 supplies the keyboard/UIA review surface, strict manifest admission, single-use default-off approval, execution revalidation and aggregate evidence store. This core parameter remains the enforcement seam beneath that UI.
- No model/runtime was downloaded, installed, registered or approved by this decision. Real Whisper/embedding quality, GPU/CPU resource measurements, license suitability and human assistive-technology acceptance remain blocked.

## Alternatives rejected

- Score caller-supplied observations without executing the backend: cannot establish provenance.
- Bind fixture paths rather than bytes: paths do not prove content and unnecessarily expose local information.
- Permit caller-supplied capture time: enables stale approval replay.
- Run all embedding inputs as one unbounded request or parallel model loads: violates adapter and resource limits.
