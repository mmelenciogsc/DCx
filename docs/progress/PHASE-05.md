# P05 — BOSS orchestration and specialist roles

- Status: Complete (deterministic benchmark gate)
- Implementation commits: `ce7ea0e`, `53af804`

Required gate: deterministic-first benchmark suite, bounded retries/budgets, route reasons and measured token/latency evidence.

## Implemented

- Validated acyclic task graphs with explicit dependencies and topological execution.
- PLANNER, RESEARCHER, CODER, DEBUGGER, REVIEWER, VALIDATOR, EXECUTOR and SECURITY_REVIEWER role configurations without forcing one model call per role.
- Capability routing order: deterministic tool, vetted CCB, local model, then OpenAI. Model-required tasks skip non-model routes; privacy-denied tasks exclude remote OpenAI.
- Per-task maximum attempts, elapsed time and tokens plus an aggregate run-token circuit breaker.
- Required-evidence checks. A nominally successful capability cannot complete a task without every declared evidence item.
- Cancellation and timeout outcomes, dependency-failure skip behavior, bounded retries and sanitized exception classification.
- Append-only JSONL checkpoints containing route reason, role, attempts, evidence names, measured token counts and elapsed milliseconds. Prompts, outputs and credentials are excluded.
- Executable benchmark with deterministic, CCB, local-model and remote-provider fixtures.
- CI correction: Windows PowerShell now throws immediately when restore, build or tests return a nonzero exit code.
- UIA smoke readiness correction: the gate waits for the complete required accessibility tree instead of only a window handle.

## Reproduction

```bash
cd /mnt/d/DCx
./scripts/verify-p00.sh
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\\DCx\\scripts\\validate-p05.ps1
```

Run `validate-p01.ps1` through `validate-p04.ps1` for the complete earlier-phase UIA regression.

## Measured final results

- Locked native Windows Release build: 0 warnings, 0 errors; 2.52 seconds.
- Tests: passed 50, failed 0, skipped 0; about 1 second.
- BOSS benchmark: PASS; run elapsed 273 ms in the recorded run.
- `normalize`: deterministic route, 1 attempt, 0 input/output tokens.
- `describe`: local-model fixture route, 1 attempt, 24 input and 11 output tokens.
- `review`: vetted-CCB route, 2 attempts; the first missing-evidence attempt was checkpointed and bounded.
- Aggregate: 24 input and 11 output tokens; four append-only attempt checkpoints; zero remote-provider calls.
- Independent tests cover graph cycles/order, deterministic precedence, model filtering, remote privacy denial, evidence retries, per-task token failure, run-token circuit breaking, elapsed timeout and caller cancellation.
- P00 smoke and P01–P04 UIA regressions passed after the readiness fix.

These are fixture measurements, not claimed production cost or time savings.

## Changed files

- BOSS domain, routing, checkpoint and coordinator: `src/DCx.Core/Orchestration`.
- Benchmark executable: `tests/DCx.BossBenchmark`.
- Unit/security tests: `tests/DCx.Core.Tests/Orchestration`.
- Gate: `scripts/validate-p05.ps1`.
- Fail-closed build and stable smoke checks: `scripts/ci.ps1`, `scripts/smoke-desktop.ps1`.

## Known limitations carried forward

- Capabilities are deterministic fixtures at this gate. P06 adds benchmarked local model adapters; later phases add vetted CCB discovery and task-specific production adapters.
- Token usage is accepted from the capability result contract. Provider adapters must derive it from trusted response usage fields and mark unavailable measurements explicitly.
- Checkpoints are local append-only JSONL, not yet the P08 relational/private-store journal or P09 signed global record.
- The orchestrator is sequential. Safe parallel DAG scheduling is deferred until resource and focus coordination can enforce the same budgets.
