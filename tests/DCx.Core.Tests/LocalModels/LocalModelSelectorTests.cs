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
        new("embed-small", 300_000_000, false, true),
        new("embed-large", 1_000_000_000, false, true),
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

    [Fact]
    public void SelectEmbeddingModel_PrefersLeastCostModelWithinRamBudget()
    {
        var selection = new LocalModelSelector().SelectEmbeddingModel(16L * 1024 * 1024 * 1024, Models);

        Assert.Equal("embed-small", selection.Model?.Name);
        Assert.Contains("25%", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectEmbeddingModel_FailsClosedUnderLowResourceEmulation()
    {
        var selection = new LocalModelSelector().SelectEmbeddingModel(512L * 1024 * 1024, Models);

        Assert.False(selection.IsAvailable);
    }

    [Theory]
    [InlineData(true, true, true, true, false, LocalInferenceRoute.LocalGpu)]
    [InlineData(false, true, true, true, false, LocalInferenceRoute.LocalCpu)]
    [InlineData(false, false, true, true, false, LocalInferenceRoute.ApprovedRemote)]
    [InlineData(false, false, true, true, true, LocalInferenceRoute.Unavailable)]
    [InlineData(false, false, true, false, false, LocalInferenceRoute.Unavailable)]
    public void FallbackPolicy_PrefersLocalAndRequiresApprovalForRemote(
        bool gpu,
        bool cpu,
        bool internet,
        bool approved,
        bool privateData,
        LocalInferenceRoute expected)
    {
        var decision = LocalInferenceFallbackPolicy.Decide(
            new LocalInferenceAvailability(gpu, cpu, internet, approved, privateData));

        Assert.Equal(expected, decision.Route);
    }
}
