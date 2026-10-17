namespace DCx.Core.Contributions;

public static class ContributionLineageGraph
{
    public static void Validate(IReadOnlyDictionary<string, IReadOnlyList<ContributionCreditEdge>> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var (resourceId, edges) in graph)
        {
            if (string.IsNullOrWhiteSpace(resourceId) || edges.Count > 64 ||
                edges.Select(item => item.ParentResourceId).Distinct(StringComparer.Ordinal).Count() != edges.Count ||
                edges.Any(edge => string.IsNullOrWhiteSpace(edge.ParentResourceId) || edge.Weight <= 0 || edge.Weight > 1) ||
                edges.Sum(edge => edge.Weight) > 1)
            {
                throw new ContributionPolicyException("lineage_invalid");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceId in graph.Keys)
        {
            Visit(resourceId);
        }

        void Visit(string resourceId)
        {
            if (visited.Contains(resourceId))
            {
                return;
            }

            if (!visiting.Add(resourceId))
            {
                throw new ContributionPolicyException("lineage_cycle");
            }

            if (graph.TryGetValue(resourceId, out var edges))
            {
                foreach (var edge in edges)
                {
                    Visit(edge.ParentResourceId);
                }
            }

            visiting.Remove(resourceId);
            visited.Add(resourceId);
        }
    }
}
