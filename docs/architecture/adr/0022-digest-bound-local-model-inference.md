# ADR 0022: Bind local inference to unique model digests

- Status: Accepted for the installed local-model boundary
- Date: 2026-10-17

## Context

Ollama inference requests identify a model by name. On the measured Windows host, `/api/tags` returned the mutable names `qwen3-vl:4b-instruct` and `qwen3-vl:8b-instruct` more than once with different SHA-256 digests. Selecting the first row and later invoking only the mutable name did not prove which bytes ran. The loaded-process API also canonicalized a digest-qualified request to a different display name, although it retained the model digest.

## Decision

DCx admits local vision and embedding inference only when the inventory contains exactly one requested name, its 64-hex digest equals the selected digest, its capability matches the operation, and the name itself ends in that digest after a delimiter. Mutable names and duplicate names are ineligible for resource-aware selection.

The production adapter re-reads and validates this binding immediately before and after inference. A missing/malformed digest, wrong capability, duplicate name, changed binding, mismatched response model, or malformed bounded inventory fails closed. The real P06/P07 fixture uses the already-installed unique alias `llamacpp:ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f` and the same digest as its expected identity.

Benchmark resource measurement matches Ollama's loaded process by digest rather than display name and verifies bounded unload. The validation script disables redirects and proxies, retains exact TRX counters, and records ambiguous mutable names without selecting them.

## Consequences

- A mutable friendly tag cannot silently select different model bytes for an accepted inference.
- Runtime retagging during an inference causes the result to be rejected.
- Existing installations need a unique digest-qualified alias before selection.
- Digest equality proves byte identity reported by the local runtime, not publisher authenticity or license approval. A production model catalog, accessible install/approval settings, signed provenance and update/revocation policy remain required.
- No model was downloaded, retagged or installed by this milestone.

## Alternatives rejected

- First matching tag: duplicate mutable names make the result order-dependent.
- Name-only allowlisting: a tag can be rebound to different bytes.
- Loaded-process display-name matching: Ollama canonicalizes that name, while its digest remains stable for measurement.
