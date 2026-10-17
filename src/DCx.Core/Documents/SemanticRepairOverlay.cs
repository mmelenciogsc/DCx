namespace DCx.Core.Documents;

public sealed class SemanticRepairOverlay
{
    public AccessibleDocument ApplyLabels(AccessibleDocument document, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(labels);
        var knownIds = document.Blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
        if (labels.Keys.Any(id => !knownIds.Contains(id)))
            throw new ArgumentException("A semantic repair references an unknown element.", nameof(labels));
        if (labels.Values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Semantic repair labels cannot be empty.", nameof(labels));

        return document with
        {
            Blocks = document.Blocks.Select(block => labels.TryGetValue(block.Id, out var label)
                ? block with { AccessibleLabel = label.Trim() }
                : block).ToArray(),
        };
    }
}
