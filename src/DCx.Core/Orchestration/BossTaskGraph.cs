namespace DCx.Core.Orchestration;

public sealed class BossTaskGraph
{
    public BossTaskGraph(IEnumerable<BossTaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        Tasks = tasks.ToArray();
        if (Tasks.Count == 0)
        {
            throw new ArgumentException("A BOSS task graph requires at least one task.", nameof(tasks));
        }

        Validate();
    }

    public IReadOnlyList<BossTaskDefinition> Tasks { get; }

    public IReadOnlyList<BossTaskDefinition> TopologicalOrder()
    {
        var byId = Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BossTaskDefinition>(Tasks.Count);

        void Visit(BossTaskDefinition task)
        {
            if (visited.Contains(task.Id)) return;
            if (!visiting.Add(task.Id))
            {
                throw new InvalidOperationException("The BOSS task graph contains a dependency cycle.");
            }

            foreach (var dependency in task.Dependencies)
            {
                Visit(byId[dependency]);
            }

            visiting.Remove(task.Id);
            visited.Add(task.Id);
            result.Add(task);
        }

        foreach (var task in Tasks)
        {
            Visit(task);
        }

        return result;
    }

    private void Validate()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in Tasks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(task.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.DisplayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.RequiredCapability);
            task.Budget.Validate();
            if (!ids.Add(task.Id))
            {
                throw new ArgumentException($"Duplicate BOSS task identifier: {task.Id}");
            }
        }

        foreach (var task in Tasks)
        {
            foreach (var dependency in task.Dependencies)
            {
                if (!ids.Contains(dependency))
                {
                    throw new ArgumentException($"Task {task.Id} depends on unknown task {dependency}.");
                }
            }
        }

        _ = TopologicalOrder();
    }
}
