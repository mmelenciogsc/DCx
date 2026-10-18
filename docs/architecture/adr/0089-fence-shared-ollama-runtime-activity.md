# ADR 0089: Fence shared Ollama runtime activity without altering other workloads

- Status: Accepted for fail-closed overlap detection; exclusive external-client isolation remains blocked
- Date: 2026-10-19

## Context

ADR 0084 serializes cooperating DCx processes, and ADR 0086 verifies exact-model unload. The loopback Ollama service is nevertheless shared with clients that do not honor DCx's lease. Production vision and embedding could begin while another model was already loaded, while benchmark admission rejected only a preloaded target digest. Cleanup proved that DCx's target disappeared but did not reject an unrelated model introduced during the operation. Those gaps weakened resource attribution and created a risk that DCx would operate alongside, or appear to clean up, another client's workload.

## Decision

After acquiring the current-user DCx inference lease, every production vision, production embedding and embedding-benchmark operation must observe an empty Ollama `/api/ps` inventory three consecutive times at 250-millisecond intervals before model work. Any running model causes immediate denial. DCx sends no inference or unload request and tells the caller to retry after the runtime is quiet.

DCx unloads only its exact target. Cleanup then rejects any running model with a different digest and never attempts to unload that unrelated model. Success requires the complete running-model inventory to be empty for three consecutive observations within the existing independent 15-second cleanup deadline. The native P06 measurement applies the same pre-operation and post-operation policy.

Cancellation during the quiet preflight performs no cleanup because DCx has not started model work. Cancellation or failure after model work begins retains the independently bounded exact-target cleanup attempt. Runtime inventory is transient: DCx does not persist model names, digests, process state or observation history from this fence.

## Consequences

- Preloaded work is preserved rather than interrupted, and its presence denies DCx inference.
- Resource and unload evidence now requires a quiet shared runtime before and after each admitted operation.
- An unrelated model introduced during cleanup makes the DCx result fail closed without modifying that model.
- The fence adds a bounded 500-millisecond quiet observation window before admitted work and retains the existing post-unload observation window.
- This is detection, not exclusive isolation. Another client can begin after the last preflight observation, and a same-model concurrent request cannot be attributed through Ollama's process inventory. Production acceptance still needs an operator-controlled environment or a separately validated dedicated runtime boundary.

## Alternatives rejected

- Automatically unload every running model: DCx has no authority to interrupt another client.
- Accept unrelated preloaded models: memory and performance attribution would be ambiguous.
- Treat one empty snapshot as a quiet period: a single point observation is weaker against short-lived activity.
- Claim the cooperative lease is exclusive: non-DCx clients cannot be required to honor it.
