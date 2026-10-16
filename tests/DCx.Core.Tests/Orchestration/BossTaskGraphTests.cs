using DCx.Core.Orchestration;

namespace DCx.Core.Tests.Orchestration;

public sealed class BossTaskGraphTests
{
    [Fact]
    public void Constructor_RejectsCycle()
    {
        Assert.Throws<InvalidOperationException>(() => new BossTaskGraph(
        [
            Task("a", ["b"]),
            Task("b", ["a"]),
        ]));
    }

    [Fact]
    public void TopologicalOrder_PlacesDependenciesFirst()
    {
        var graph = new BossTaskGraph([Task("validate", ["execute"]), Task("execute", [])]);

        Assert.Equal(["execute", "validate"], graph.TopologicalOrder().Select(task => task.Id));
    }

    private static BossTaskDefinition Task(string id, IReadOnlyList<string> dependencies) =>
        new(id, id, BossSpecialistRole.Executor, "fixture", dependencies, new HashSet<string>(),
            new BossTaskBudget(1, 0, TimeSpan.FromSeconds(1)));
}
