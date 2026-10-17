# ADR 0049: Bounded, aggregate-only local-model quality evaluation

- Status: Accepted for deterministic benchmark evaluation; real Whisper/embedding acceptance remains blocked
- Date: 2026-10-18

## Context

P06 already pins local model/runtime identities and bounds their execution, but adapter success does not prove transcription accuracy or embedding retrieval quality. A future approved backend needs deterministic comparison against reviewed fixtures, explicit resource ceilings and unload evidence. Retaining transcript pairs, fixture identifiers or vectors in a benchmark report would unnecessarily expand sensitive-data handling.

## Decision

DCx evaluates transcription with aggregate word error rate and character error rate after invariant Unicode compatibility normalization, case folding and letter/digit tokenization. It returns only fixture/reference counts, aggregate substitution/deletion/insertion counts, rates, resource measurements and closed failure codes. Input fixture identifiers and expected/actual text are never included in the result. One run admits at most 32 bounded fixtures, and a global edit-distance cell budget rejects quadratic workloads before scoring.

Embedding retrieval evaluation admits at most 256 documents, 64 queries and 8,192 finite, consistently dimensioned values per vector. It rejects zero-magnitude vectors and missing, duplicate or unknown relevance labels. Cosine ranking uses ordinal identifier order for deterministic ties. Results contain only aggregate Recall@K, mean reciprocal rank, counts, dimensions, resource measurements and closed failure codes—not identifiers, text or vectors.

Both evaluators use caller-supplied reviewed thresholds. Elapsed time and peak working set are required. A GPU ceiling makes peak GPU measurement required. Confirmed unload is required by default. Missing measurements, exceeded thresholds, absent unload, malformed input, nonfinite values and excessive work fail closed.

## Consequences

- The same aggregate-only gate can evaluate a future exact-approved Whisper backend and Ollama embedding model without changing scoring semantics.
- Thresholds are not hardcoded as claims of product quality; fixture composition and acceptance limits still require review before real acceptance.
- In-memory fixture text and vectors remain sensitive to the caller even though the returned report excludes them. The caller must use generated/publicly licensed fixtures and must not log raw observations.
- Passing deterministic evaluator tests proves scoring, bounds and privacy shape only. It does not prove a real model's accuracy, suitability, license, resource use, CPU fallback or accessibility.

## Alternatives rejected

- Treat a nonempty transcript or vector response as an accuracy pass: adapter availability is not quality evidence.
- Persist raw expected/actual transcripts or vectors in evidence: unnecessary content exposure.
- Use unbounded edit distance or ranking inputs: avoidable CPU/memory denial-of-service risk.
- Select one universal quality threshold in code: fixture language, domain and product policy need explicit reviewed acceptance criteria.
