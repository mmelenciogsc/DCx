# P05 — BOSS orchestration and specialist roles

- Status: In progress
- Commands/results: none
- Changed files: none
- Commit: none

Required gate: deterministic-first benchmark suite, bounded retries/budgets, route reasons and measured token/latency evidence.

## Initial plan

Implement a provider-neutral task DAG and append-only execution evidence before any model-assisted task is enabled. Route deterministic tools and later approved CCB capabilities first; invoke a model only when a task declares that requirement and an available provider satisfies it. Enforce attempt, elapsed-time and token budgets in the coordinator. Treat PLANNER, RESEARCHER, CODER, DEBUGGER, REVIEWER, VALIDATOR, EXECUTOR and SECURITY_REVIEWER as policy configurations rather than unconditional separate inference calls. Use deterministic fixtures to prove routing, checkpoints, cancellation, retry bounds and evidence gates without claiming cost savings.
