namespace DCx.Core.Orchestration;

public sealed class BossRouter
{
    private static readonly IReadOnlyDictionary<BossRouteKind, int> Priority =
        new Dictionary<BossRouteKind, int>
        {
            [BossRouteKind.DeterministicTool] = 0,
            [BossRouteKind.VettedCcb] = 1,
            [BossRouteKind.LocalModel] = 2,
            [BossRouteKind.OpenAi] = 3,
        };

    public BossRouteDecision Select(BossTaskDefinition task, IEnumerable<IBossCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(capabilities);
        var candidates = capabilities
            .Where(capability => capability.IsAvailable && capability.Supports(task.RequiredCapability))
            .Where(capability => !task.RequiresModel || capability.RouteKind is BossRouteKind.LocalModel or BossRouteKind.OpenAi)
            .Where(capability => task.AllowRemoteProvider || capability.RouteKind != BossRouteKind.OpenAi)
            .OrderBy(capability => Priority[capability.RouteKind])
            .ThenBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            var privacy = task.AllowRemoteProvider ? string.Empty : " Remote providers were excluded by task privacy policy.";
            return new BossRouteDecision(null, $"No available capability satisfies {task.RequiredCapability}.{privacy}".Trim());
        }

        var selected = candidates[0];
        return new BossRouteDecision(
            selected,
            $"Selected {selected.RouteKind} capability {selected.Id}; deterministic, vetted CCB, local model, then OpenAI precedence applied.");
    }
}
