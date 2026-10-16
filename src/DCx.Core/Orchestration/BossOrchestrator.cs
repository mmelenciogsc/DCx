using System.Diagnostics;

namespace DCx.Core.Orchestration;

public sealed class BossOrchestrator(
    IEnumerable<IBossCapability> capabilities,
    IBossCheckpointStore checkpointStore,
    BossRouter? router = null)
{
    private readonly IBossCapability[] _capabilities = capabilities?.ToArray()
        ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IBossCheckpointStore _checkpointStore = checkpointStore
        ?? throw new ArgumentNullException(nameof(checkpointStore));
    private readonly BossRouter _router = router ?? new BossRouter();

    public async Task<BossRunResult> RunAsync(
        BossTaskGraph graph,
        int maximumRunTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (maximumRunTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRunTokens));
        }

        var runId = Guid.NewGuid().ToString("N");
        var runClock = Stopwatch.StartNew();
        var outcomes = new Dictionary<string, BossTaskOutcome>(StringComparer.Ordinal);
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var runTokenBudgetExceeded = false;

        foreach (var task in graph.TopologicalOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runTokenBudgetExceeded)
            {
                var budgetSkipped = new BossTaskOutcome(
                    task.Id,
                    BossTaskStatus.Skipped,
                    task.Role,
                    null,
                    "Skipped because the run token circuit breaker is open.",
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    new HashSet<string>(),
                    "run_token_budget_exceeded");
                outcomes.Add(task.Id, budgetSkipped);
                await RecordAsync(runId, budgetSkipped, attempt: 0, cancellationToken);
                continue;
            }

            var failedDependency = task.Dependencies.FirstOrDefault(id =>
                outcomes[id].Status != BossTaskStatus.Completed);
            if (failedDependency is not null)
            {
                var skipped = new BossTaskOutcome(
                    task.Id,
                    BossTaskStatus.Skipped,
                    task.Role,
                    null,
                    $"Skipped because dependency {failedDependency} did not complete.",
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    new HashSet<string>(),
                    "dependency_failed");
                outcomes.Add(task.Id, skipped);
                await RecordAsync(runId, skipped, attempt: 0, cancellationToken);
                continue;
            }

            var route = _router.Select(task, _capabilities);
            if (!route.IsAvailable)
            {
                var unavailable = new BossTaskOutcome(
                    task.Id,
                    BossTaskStatus.Failed,
                    task.Role,
                    null,
                    route.Reason,
                    0,
                    0,
                    0,
                    TimeSpan.Zero,
                    new HashSet<string>(),
                    "capability_unavailable");
                outcomes.Add(task.Id, unavailable);
                await RecordAsync(runId, unavailable, attempt: 0, cancellationToken);
                continue;
            }

            var taskClock = Stopwatch.StartNew();
            var taskInputTokens = 0;
            var taskOutputTokens = 0;
            BossTaskOutcome? final = null;
            using var taskTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            taskTimeout.CancelAfter(task.Budget.MaximumElapsed);

            for (var attempt = 1; attempt <= task.Budget.MaximumAttempts; attempt++)
            {
                try
                {
                    var context = new BossTaskContext(task.Id, task.DisplayName, task.Role, attempt, outcomes);
                    var result = await route.Capability!.ExecuteAsync(context, taskTimeout.Token);
                    taskInputTokens += result.InputTokens;
                    taskOutputTokens += result.OutputTokens;
                    totalInputTokens += result.InputTokens;
                    totalOutputTokens += result.OutputTokens;
                    var taskTokens = taskInputTokens + taskOutputTokens;
                    var runTokens = totalInputTokens + totalOutputTokens;
                    var missingEvidence = task.RequiredEvidence.Except(result.Evidence, StringComparer.Ordinal).ToArray();
                    var failureCode = result.FailureCode;
                    var completed = result.Succeeded && missingEvidence.Length == 0;
                    if (taskTokens > task.Budget.MaximumTokens || runTokens > maximumRunTokens)
                    {
                        completed = false;
                        failureCode = "token_budget_exceeded";
                        runTokenBudgetExceeded = runTokens > maximumRunTokens;
                    }
                    else if (result.Succeeded && missingEvidence.Length > 0)
                    {
                        failureCode = "evidence_missing";
                    }

                    var attemptOutcome = new BossTaskOutcome(
                        task.Id,
                        completed ? BossTaskStatus.Completed : BossTaskStatus.Failed,
                        task.Role,
                        route.Capability.RouteKind,
                        route.Reason,
                        attempt,
                        taskInputTokens,
                        taskOutputTokens,
                        taskClock.Elapsed,
                        result.Evidence,
                        failureCode ?? (completed ? null : "capability_failed"));
                    await RecordAsync(runId, attemptOutcome, attempt, cancellationToken);
                    if (completed || failureCode == "token_budget_exceeded")
                    {
                        final = attemptOutcome;
                        break;
                    }

                    final = attemptOutcome;
                }
                catch (OperationCanceledException) when (taskTimeout.IsCancellationRequested)
                {
                    final = new BossTaskOutcome(
                        task.Id,
                        cancellationToken.IsCancellationRequested ? BossTaskStatus.Cancelled : BossTaskStatus.Failed,
                        task.Role,
                        route.Capability!.RouteKind,
                        route.Reason,
                        attempt,
                        taskInputTokens,
                        taskOutputTokens,
                        taskClock.Elapsed,
                        new HashSet<string>(),
                        cancellationToken.IsCancellationRequested ? "cancelled" : "time_budget_exceeded");
                    await RecordAsync(runId, final, attempt, CancellationToken.None);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        outcomes.Add(task.Id, final);
                        return CreateRunResult(graph, outcomes, totalInputTokens, totalOutputTokens, runClock.Elapsed);
                    }

                    break;
                }
                catch (Exception)
                {
                    final = new BossTaskOutcome(
                        task.Id,
                        BossTaskStatus.Failed,
                        task.Role,
                        route.Capability!.RouteKind,
                        route.Reason,
                        attempt,
                        taskInputTokens,
                        taskOutputTokens,
                        taskClock.Elapsed,
                        new HashSet<string>(),
                        "capability_exception");
                    await RecordAsync(runId, final, attempt, cancellationToken);
                }
            }

            outcomes.Add(task.Id, final ?? throw new InvalidOperationException("BOSS task produced no final outcome."));
        }

        return CreateRunResult(graph, outcomes, totalInputTokens, totalOutputTokens, runClock.Elapsed);
    }

    private Task RecordAsync(string runId, BossTaskOutcome outcome, int attempt, CancellationToken cancellationToken) =>
        _checkpointStore.AppendAsync(
            new BossCheckpoint(
                runId,
                outcome.TaskId,
                attempt,
                outcome.Status.ToString(),
                outcome.Role,
                outcome.Route,
                outcome.RouteReason,
                outcome.InputTokens,
                outcome.OutputTokens,
                (long)outcome.Elapsed.TotalMilliseconds,
                outcome.Evidence.Order(StringComparer.Ordinal).ToArray(),
                outcome.FailureCode,
                DateTimeOffset.UtcNow),
            cancellationToken);

    private static BossRunResult CreateRunResult(
        BossTaskGraph graph,
        IReadOnlyDictionary<string, BossTaskOutcome> outcomes,
        int inputTokens,
        int outputTokens,
        TimeSpan elapsed)
    {
        var ordered = graph.TopologicalOrder()
            .Where(task => outcomes.ContainsKey(task.Id))
            .Select(task => outcomes[task.Id])
            .ToArray();
        return new BossRunResult(ordered, inputTokens, outputTokens, elapsed);
    }
}
