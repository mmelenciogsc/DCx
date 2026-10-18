namespace DCx.Core.LocalModels;

public sealed class LocalModelSelector
{
    private const double MaximumVramFraction = 0.70;
    private const double MaximumEmbeddingRamFraction = 0.25;

    public LocalModelResourceSelection SelectForHardware(
        LocalHardwareProfile hardware,
        IEnumerable<LocalModelDescriptor> installedModels)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(installedModels);
        var inventory = installedModels.Where(model => model is not null).ToArray();
        var graphicsMemory = hardware.Gpus
            .Where(gpu => gpu.LocalModelGpuAdmissionEligible && gpu.AvailableMemoryBytes is > 0)
            .Select(gpu => gpu.AvailableMemoryBytes!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return new LocalModelResourceSelection(
            graphicsMemory,
            hardware.AvailableMemoryBytes,
            SelectVisionModel(graphicsMemory, inventory),
            SelectEmbeddingModel(hardware.AvailableMemoryBytes, inventory));
    }

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
            ? new LocalModelSelection(null, $"No uniquely digest-bound installed vision model fits the {budget} byte usable-VRAM safety budget.")
            : new LocalModelSelection(
                selected,
                $"Selected the largest uniquely digest-bound installed vision model within 70% of currently available GPU memory: {selected.Name} ({selected.SizeBytes} bytes).");
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
            ? new LocalModelSelection(null, $"No uniquely digest-bound installed embedding model fits the {budget} byte available-RAM safety budget.")
            : new LocalModelSelection(
                selected,
                $"Selected the smallest uniquely digest-bound installed embedding model within 25% of currently available system memory: {selected.Name} ({selected.SizeBytes} bytes).");
    }
}

public sealed record LocalModelResourceSelection(
    long AvailableGraphicsMemoryBytes,
    long AvailableSystemMemoryBytes,
    LocalModelSelection Vision,
    LocalModelSelection Embedding);
