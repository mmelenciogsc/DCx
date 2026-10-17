namespace DCx.Core.LocalModels;

public sealed class LocalModelSelector
{
    private const double MaximumVramFraction = 0.70;
    private const double MaximumEmbeddingRamFraction = 0.25;

    public LocalModelSelection SelectVisionModel(long availableVramBytes, IEnumerable<LocalModelDescriptor> installedModels)
    {
        if (availableVramBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableVramBytes));
        }

        ArgumentNullException.ThrowIfNull(installedModels);
        var inventory = installedModels.Where(model => model is not null).ToArray();
        var budget = (long)(availableVramBytes * MaximumVramFraction);
        var selected = inventory
            .Where(model => model.HasDigestBoundName &&
                inventory.Count(candidate => string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)) == 1 &&
                model.SupportsVision && model.SizeBytes > 0 && model.SizeBytes <= budget)
            .OrderByDescending(model => model.SizeBytes)
            .ThenBy(model => model.Name, StringComparer.Ordinal)
            .ThenBy(model => model.Digest, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected is null
            ? new LocalModelSelection(null, $"No uniquely digest-bound installed vision model fits the {budget} byte VRAM safety budget.")
            : new LocalModelSelection(
                selected,
                $"Selected the largest uniquely digest-bound installed vision model within 70% of measured VRAM: {selected.Name} ({selected.SizeBytes} bytes).");
    }

    public LocalModelSelection SelectEmbeddingModel(
        long availableRamBytes,
        IEnumerable<LocalModelDescriptor> installedModels)
    {
        if (availableRamBytes < 0) throw new ArgumentOutOfRangeException(nameof(availableRamBytes));
        ArgumentNullException.ThrowIfNull(installedModels);
        var inventory = installedModels.Where(model => model is not null).ToArray();
        var budget = (long)(availableRamBytes * MaximumEmbeddingRamFraction);
        var selected = inventory
            .Where(model => model.HasDigestBoundName &&
                inventory.Count(candidate => string.Equals(candidate.Name, model.Name, StringComparison.Ordinal)) == 1 &&
                model.SupportsEmbeddings && model.SizeBytes > 0 && model.SizeBytes <= budget)
            .OrderBy(model => model.SizeBytes)
            .ThenBy(model => model.Name, StringComparer.Ordinal)
            .ThenBy(model => model.Digest, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected is null
            ? new LocalModelSelection(null, $"No uniquely digest-bound installed embedding model fits the {budget} byte RAM safety budget.")
            : new LocalModelSelection(
                selected,
                $"Selected the smallest uniquely digest-bound installed embedding model within 25% of measured RAM: {selected.Name} ({selected.SizeBytes} bytes).");
    }
}
