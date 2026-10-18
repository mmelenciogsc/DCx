# ADR 0052: Bind Ollama embedding benchmarks to exact runtime, loaded resources and unload

- Status: Accepted for already-installed approved models; shared-runtime fencing is extended by ADR 0089 and real model acceptance remains blocked
- Date: 2026-10-18

## Context

ADRs 0049–0051 admit, approve, score and retain aggregate embedding benchmarks, but previously had no concrete production Ollama adapter. Treating a fixture adapter as production evidence would hide runtime drift, preloaded resource attribution, mutable model identity and failed unloads.

## Decision

The embedding benchmark requires a separately approved, digest-qualified Ollama model with its exact inspected license-text digest. Review revalidates that identity and capability, reads the bounded loopback `/api/version`, and binds model digest, license digest, hashed model name and hashed runtime version into the backend digest.

Execution revalidates that binding. ADR 0089 now requires three empty `/api/ps` observations and refuses any preloaded model, avoiding an obviously non-attributable resource sample. `/api/embed` uses `truncate:false`, at most 32 bounded inputs and a one-minute keep-alive so the exact target can be observed after inference. The response model, vector count/dimensions, finite values and duration/token counters are validated. The approved binding and runtime version are checked around the call.

The post-call `/api/ps` response must contain exactly one matching name and digest. Its documented `size` and `size_vram` values become the loaded-model memory and GPU policy observations. The existing aggregate evidence property is named `peakWorkingSetBytes`; for this adapter it contains Ollama's loaded-model `size`, not an operating-system process peak. The accessible UI says “loaded memory” to preserve that distinction.

An independent 15-second cleanup token always sends `/api/generate` with the exact model and `keep_alive:0`. The response must identify the model and report `done:true` with `done_reason:"unload"`. Three consecutive `/api/ps` observations must then show a completely empty runtime. An unrelated model is rejected and left untouched. Cancellation still attempts this cleanup. A preloaded workload is not unloaded because execution never claims ownership of it.

All requests remain on the existing hardened exact loopback HTTP origin with redirects, cookies and proxies disabled. No model download, install, pull, retag, upload or production selection change is authorized.

## Consequences

- Deterministic fixtures prove runtime drift, malformed resource data, preloaded-model, cancellation and unload denials plus aggregate-only runner composition.
- Native UIA proves an embedding manifest remains reviewable but approval and Run stay disabled when no approved model exists.
- The quiet fence detects preloaded and post-operation overlap, but an external client could still load the same model inside the operation. A real benchmark must use an operator-controlled environment or separately validated dedicated runtime and record this limitation.
- This host has no installed Ollama embedding model. No real Recall@K/MRR, latency, memory, GPU, license or general-quality acceptance is claimed, so P06 remains blocked.

## Alternatives rejected

- Use fixture vectors as production evidence: does not establish real model/runtime behavior.
- Benchmark while any model is already loaded: shared-runtime resources cannot be attributed to this run.
- Trust an unload response without observing absence: permits late or ineffective unload behavior.
- Call Ollama cloud or pull a model automatically: exceeds local-only scope and installation/network authority.
