using DCx.Core.Orchestration;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DCx.BossBenchmark <checkpoint-path>");
    return 2;
}

var checkpointPath = Path.GetFullPath(args[0]);
using var checkpointStore = new JsonLineBossCheckpointStore(checkpointPath);
var reviewAttempts = 0;
IBossCapability[] capabilities =
[
    new FixtureCapability("deterministic.normalize", BossRouteKind.DeterministicTool, Set("normalize.url"),
        (_, _) => Task.FromResult(new BossCapabilityResult(true, Set("normalized_url")))),
    new FixtureCapability("ccb.review", BossRouteKind.VettedCcb, Set("review.claim"),
        (_, _) => Task.FromResult(new BossCapabilityResult(
            true,
            ++reviewAttempts == 1 ? Set("draft_review") : Set("validated_review")))),
    new FixtureCapability("local.describe", BossRouteKind.LocalModel, Set("describe.visual"),
        async (_, cancellationToken) =>
        {
            await Task.Delay(25, cancellationToken);
            return new BossCapabilityResult(true, Set("description"), InputTokens: 24, OutputTokens: 11);
        }),
    new FixtureCapability("remote.describe", BossRouteKind.OpenAi, Set("describe.visual"),
        (_, _) => Task.FromResult(new BossCapabilityResult(true, Set("description"), InputTokens: 20, OutputTokens: 9))),
];

var graph = new BossTaskGraph(
[
    new BossTaskDefinition(
        "normalize", "Normalize fixture URL", BossSpecialistRole.Executor, "normalize.url", [], Set("normalized_url"),
        new BossTaskBudget(1, 0, TimeSpan.FromSeconds(2))),
    new BossTaskDefinition(
        "describe", "Describe fixture", BossSpecialistRole.Researcher, "describe.visual", ["normalize"], Set("description"),
        new BossTaskBudget(1, 100, TimeSpan.FromSeconds(2)), RequiresModel: true, AllowRemoteProvider: true),
    new BossTaskDefinition(
        "review", "Review fixture claim", BossSpecialistRole.Validator, "review.claim", ["describe"], Set("validated_review"),
        new BossTaskBudget(2, 0, TimeSpan.FromSeconds(2))),
]);

var orchestrator = new BossOrchestrator(capabilities, checkpointStore);
var result = await orchestrator.RunAsync(graph, maximumRunTokens: 100);
if (!result.Succeeded || result.Tasks.Count != 3 || reviewAttempts != 2)
{
    Console.Error.WriteLine("BOSS benchmark failed its completion or retry assertions.");
    return 1;
}

foreach (var task in result.Tasks)
{
    Console.WriteLine(
        $"task={task.TaskId};status={task.Status};role={task.Role};route={task.Route};attempts={task.Attempts};" +
        $"input_tokens={task.InputTokens};output_tokens={task.OutputTokens};elapsed_ms={(long)task.Elapsed.TotalMilliseconds};" +
        $"reason={task.RouteReason}");
}

Console.WriteLine($"run_status=PASS;input_tokens={result.InputTokens};output_tokens={result.OutputTokens};elapsed_ms={(long)result.Elapsed.TotalMilliseconds}");
Console.WriteLine($"checkpoint={checkpointPath}");
return 0;

static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

internal sealed class FixtureCapability(
    string id,
    BossRouteKind routeKind,
    IReadOnlySet<string> supported,
    Func<BossTaskContext, CancellationToken, Task<BossCapabilityResult>> execute) : IBossCapability
{
    public string Id { get; } = id;
    public BossRouteKind RouteKind { get; } = routeKind;
    public bool IsAvailable => true;
    public bool Supports(string capability) => supported.Contains(capability);
    public Task<BossCapabilityResult> ExecuteAsync(BossTaskContext context, CancellationToken cancellationToken) =>
        execute(context, cancellationToken);
}
