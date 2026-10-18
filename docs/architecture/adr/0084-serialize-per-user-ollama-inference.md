# ADR 0084: Serialize per-user Ollama inference

- Status: Accepted for cooperative DCx local-model operations; shared-runtime detection is extended by ADR 0089 while exclusive external-client isolation remains blocked
- Date: 2026-10-19

## Context

DCx already ran benchmark fixtures sequentially and verified explicit Ollama unload. Separate DCx processes could nevertheless issue vision, embedding or benchmark requests to the same per-user Ollama runtime concurrently. That weakened memory budgeting and made loaded-model and unload attribution ambiguous. The benchmark's preloaded-model check alone did not cover the interval between that check and final unload verification.

## Decision

All production DCx Ollama vision, embedding and embedding-benchmark operations acquire one current-user inference lease before the first provider request. The benchmark retains that lease across version checks, preload inspection, inference, resource sampling, explicit unload, three consecutive absence observations and the final version check.

The coordinator combines a process-local semaphore with exclusive access to an empty persistent lock file at `%LOCALAPPDATA%\DCx\LocalModels\OllamaCoordination\.dcx-ollama-inference.lock`. Acquisition is cancellable and bounded to five minutes. Release or process termination makes the lease recoverable. Pre-cancelled acquisition creates no storage.

The normal local-model path guard applies. A lock path that is a directory, a reparse point or a nonempty file is rejected without truncation or mutation. The file contains no owner identity, process ID, model name, prompt, response or path.

## Consequences

- Cooperating DCx processes cannot overlap production Ollama vision, embedding or benchmark operations for one user.
- Normal embedding and vision calls hold the lease across all identity checks, inference and unload work.
- Benchmark resource and unload evidence belongs to one complete DCx operation rather than an interleaving DCx operation.
- Cancellation while queued does not enter the provider boundary; release and abnormal process exit permit later recovery.
- The lock is advisory. ADR 0089 adds non-destructive three-observation quiet-runtime checks before and after work, but an unrelated Ollama client can still begin inside the operation; stronger runtime isolation remains required for production acceptance.
- A malicious process already running as the same Windows user remains outside this cooperative boundary, and ordinary path revalidation cannot eliminate every same-user check/open race.

## Alternatives rejected

- A static semaphore alone: it cannot coordinate separate DCx processes.
- A named mutex alone: its access-control and stale/identity diagnostics add platform complexity without removing the need for confined per-user storage.
- Lock only the inference request: preflight, resource sampling and unload could still be attributed to another operation.
- Truncate a pre-existing lock file: a hostile link or unexpected file could turn coordination into data mutation.
