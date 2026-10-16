using DCx.Core.Orchestration;

namespace DCx.Core.Tests.Orchestration;

public sealed class BossRouterTests
{
    [Fact]
    public void Select_PrefersDeterministicThenCcbThenLocalThenRemote()
    {
        var router = new BossRouter();
        var task = Task(requiresModel: false, allowRemote: true);
        var capabilities = new IBossCapability[]
        {
            new StubCapability("remote", BossRouteKind.OpenAi),
            new StubCapability("local", BossRouteKind.LocalModel),
            new StubCapability("ccb", BossRouteKind.VettedCcb),
            new StubCapability("deterministic", BossRouteKind.DeterministicTool),
        };

        var route = router.Select(task, capabilities);

        Assert.Equal("deterministic", route.Capability?.Id);
        Assert.Contains("precedence", route.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_ModelTaskPrefersLocalAndHonorsRemotePrivacyDenial()
    {
        var router = new BossRouter();
        var local = new StubCapability("local", BossRouteKind.LocalModel);
        var remote = new StubCapability("remote", BossRouteKind.OpenAi);

        Assert.Equal("local", router.Select(Task(true, false), [remote, local]).Capability?.Id);
        Assert.False(router.Select(Task(true, false), [remote]).IsAvailable);
        Assert.Contains("privacy", router.Select(Task(true, false), [remote]).Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static BossTaskDefinition Task(bool requiresModel, bool allowRemote) =>
        new("task", "Task", BossSpecialistRole.Planner, "fixture", [], new HashSet<string>(),
            new BossTaskBudget(1, 100, TimeSpan.FromSeconds(1)), requiresModel, allowRemote);

    private sealed class StubCapability(string id, BossRouteKind route) : IBossCapability
    {
        public string Id { get; } = id;
        public BossRouteKind RouteKind { get; } = route;
        public bool IsAvailable => true;
        public bool Supports(string capability) => capability == "fixture";
        public Task<BossCapabilityResult> ExecuteAsync(BossTaskContext context, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new BossCapabilityResult(true, new HashSet<string>()));
    }
}
