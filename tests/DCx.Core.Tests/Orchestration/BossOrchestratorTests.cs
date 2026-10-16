using DCx.Core.Orchestration;

namespace DCx.Core.Tests.Orchestration;

public sealed class BossOrchestratorTests
{
    [Fact]
    public async Task Run_RetriesMissingEvidenceWithinBoundAndCheckpointsEveryAttempt()
    {
        var attempts = 0;
        var capability = new DelegateCapability(async (_, _) =>
        {
            await Task.Yield();
            attempts++;
            return new BossCapabilityResult(true, attempts == 1 ? Set("draft") : Set("accepted"), 2, 1);
        });
        var checkpoints = new MemoryCheckpointStore();
        var orchestrator = new BossOrchestrator([capability], checkpoints);
        var graph = Graph(new BossTaskBudget(2, 10, TimeSpan.FromSeconds(1)), Set("accepted"));

        var result = await orchestrator.RunAsync(graph, 10);

        var task = Assert.Single(result.Tasks);
        Assert.Equal(BossTaskStatus.Completed, task.Status);
        Assert.Equal(2, task.Attempts);
        Assert.Equal(6, task.InputTokens + task.OutputTokens);
        Assert.Equal(2, checkpoints.Items.Count);
        Assert.Equal("evidence_missing", checkpoints.Items[0].FailureCode);
    }

    [Fact]
    public async Task Run_StopsRetryingWhenTokenBudgetIsExceeded()
    {
        var attempts = 0;
        var capability = new DelegateCapability((_, _) =>
        {
            attempts++;
            return Task.FromResult(new BossCapabilityResult(true, Set("accepted"), 8, 5));
        });
        var orchestrator = new BossOrchestrator([capability], new MemoryCheckpointStore());

        var result = await orchestrator.RunAsync(Graph(new BossTaskBudget(3, 10, TimeSpan.FromSeconds(1)), Set("accepted")), 100);

        Assert.Equal("token_budget_exceeded", Assert.Single(result.Tasks).FailureCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Run_EnforcesElapsedBudget()
    {
        var capability = new DelegateCapability(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new BossCapabilityResult(true, Set("accepted"));
        });
        var orchestrator = new BossOrchestrator([capability], new MemoryCheckpointStore());

        var result = await orchestrator.RunAsync(
            Graph(new BossTaskBudget(1, 10, TimeSpan.FromMilliseconds(50)), Set("accepted")),
            100);

        Assert.Equal("time_budget_exceeded", Assert.Single(result.Tasks).FailureCode);
    }

    [Fact]
    public async Task Run_OpensCircuitBreakerForRemainingTasksAfterRunTokenBudget()
    {
        var executions = 0;
        var capability = new DelegateCapability((_, _) =>
        {
            executions++;
            return Task.FromResult(new BossCapabilityResult(true, Set("accepted"), 6, 5));
        });
        var orchestrator = new BossOrchestrator([capability], new MemoryCheckpointStore());
        var budget = new BossTaskBudget(1, 20, TimeSpan.FromSeconds(1));
        var graph = new BossTaskGraph(
        [
            new BossTaskDefinition("first", "First", BossSpecialistRole.Executor, "fixture", [], Set("accepted"), budget),
            new BossTaskDefinition("second", "Second", BossSpecialistRole.Executor, "fixture", [], Set("accepted"), budget),
        ]);

        var result = await orchestrator.RunAsync(graph, maximumRunTokens: 10);

        Assert.Equal(1, executions);
        Assert.Equal("token_budget_exceeded", result.Tasks[0].FailureCode);
        Assert.Equal("run_token_budget_exceeded", result.Tasks[1].FailureCode);
        Assert.Equal(BossTaskStatus.Skipped, result.Tasks[1].Status);
    }

    private static BossTaskGraph Graph(BossTaskBudget budget, IReadOnlySet<string> evidence) =>
        new([new BossTaskDefinition("task", "Task", BossSpecialistRole.Validator, "fixture", [], evidence, budget)]);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private sealed class DelegateCapability(
        Func<BossTaskContext, CancellationToken, Task<BossCapabilityResult>> execute) : IBossCapability
    {
        public string Id => "fixture";
        public BossRouteKind RouteKind => BossRouteKind.DeterministicTool;
        public bool IsAvailable => true;
        public bool Supports(string capability) => capability == "fixture";
        public Task<BossCapabilityResult> ExecuteAsync(BossTaskContext context, CancellationToken cancellationToken) =>
            execute(context, cancellationToken);
    }

    private sealed class MemoryCheckpointStore : IBossCheckpointStore
    {
        public List<BossCheckpoint> Items { get; } = [];

        public Task AppendAsync(BossCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Items.Add(checkpoint);
            return Task.CompletedTask;
        }
    }
}
