using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelSelectorTests
{
    private static readonly LocalModelDescriptor[] Models =
    [
        new("vision-2b", 2_000_000_000, true, false),
        new("vision-4b", 3_500_000_000, true, false),
        new("vision-8b", 6_500_000_000, true, false),
        new("text-only", 1_000_000_000, false, false),
    ];

    [Fact]
    public void SelectVisionModel_UsesLargestInstalledModelWithinSafetyBudget()
    {
        var selection = new LocalModelSelector().SelectVisionModel(8_188L * 1024 * 1024, Models);

        Assert.Equal("vision-4b", selection.Model?.Name);
        Assert.Contains("70%", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectVisionModel_UsesSmallerFallbackOnConstrainedGpu()
    {
        var selection = new LocalModelSelector().SelectVisionModel(4_096L * 1024 * 1024, Models);

        Assert.Equal("vision-2b", selection.Model?.Name);
    }

    [Fact]
    public void SelectVisionModel_FailsClosedWhenNothingFits()
    {
        var selection = new LocalModelSelector().SelectVisionModel(1_024L * 1024 * 1024, Models);

        Assert.False(selection.IsAvailable);
    }
}
