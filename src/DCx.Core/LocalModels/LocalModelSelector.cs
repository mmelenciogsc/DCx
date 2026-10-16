namespace DCx.Core.LocalModels;

public sealed class LocalModelSelector
{
    private const double MaximumVramFraction = 0.70;

    public LocalModelSelection SelectVisionModel(long availableVramBytes, IEnumerable<LocalModelDescriptor> installedModels)
    {
        if (availableVramBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableVramBytes));
        }

        ArgumentNullException.ThrowIfNull(installedModels);
        var budget = (long)(availableVramBytes * MaximumVramFraction);
        var selected = installedModels
            .Where(model => model.SupportsVision && model.SizeBytes > 0 && model.SizeBytes <= budget)
            .OrderByDescending(model => model.SizeBytes)
            .ThenBy(model => model.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected is null
            ? new LocalModelSelection(null, $"No installed vision model fits the {budget} byte VRAM safety budget.")
            : new LocalModelSelection(
                selected,
                $"Selected the largest installed vision model within 70% of measured VRAM: {selected.Name} ({selected.SizeBytes} bytes).");
    }
}
