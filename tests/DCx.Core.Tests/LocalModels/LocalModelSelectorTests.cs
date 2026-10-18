using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelSelectorTests
{
    private const string Vision2Digest = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Vision4Digest = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Vision8Digest = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string TextDigest = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string EmbedSmallDigest = "5555555555555555555555555555555555555555555555555555555555555555";
    private const string EmbedLargeDigest = "6666666666666666666666666666666666666666666666666666666666666666";
    private static readonly LocalModelDescriptor[] Models =
    [
        new("vision-2b:" + Vision2Digest, 2_000_000_000, true, false, Digest: Vision2Digest),
        new("vision-4b:" + Vision4Digest, 3_500_000_000, true, false, Digest: Vision4Digest),
        new("vision-8b:" + Vision8Digest, 6_500_000_000, true, false, Digest: Vision8Digest),
        new("text-only:" + TextDigest, 1_000_000_000, false, false, Digest: TextDigest),
        new("embed-small:" + EmbedSmallDigest, 300_000_000, false, true, Digest: EmbedSmallDigest),
        new("embed-large:" + EmbedLargeDigest, 1_000_000_000, false, true, Digest: EmbedLargeDigest),
    ];

    [Fact]
    public void SelectVisionModel_UsesLargestInstalledModelWithinSafetyBudget()
    {
        var selection = new LocalModelSelector().SelectVisionModel(8_188L * 1024 * 1024, Models);

        Assert.Equal("vision-4b:" + Vision4Digest, selection.Model?.Name);
        Assert.Contains("70%", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectVisionModel_UsesSmallerFallbackOnConstrainedGpu()
    {
        var selection = new LocalModelSelector().SelectVisionModel(4_096L * 1024 * 1024, Models);

        Assert.Equal("vision-2b:" + Vision2Digest, selection.Model?.Name);
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

        Assert.Equal("embed-small:" + EmbedSmallDigest, selection.Model?.Name);
        Assert.Contains("25%", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectEmbeddingModel_FailsClosedUnderLowResourceEmulation()
    {
        var selection = new LocalModelSelector().SelectEmbeddingModel(512L * 1024 * 1024, Models);

        Assert.False(selection.IsAvailable);
    }

    [Fact]
    public void Selection_RejectsMutableOrAmbiguousModelNames()
    {
        var ambiguous = new LocalModelDescriptor[]
        {
            new("mutable-tag", 1_000, true, false, Digest: Vision2Digest),
            new("duplicate:" + Vision4Digest, 2_000, true, false, Digest: Vision4Digest),
            new("duplicate:" + Vision4Digest, 2_000, true, false, Digest: Vision4Digest),
        };

        var selection = new LocalModelSelector().SelectVisionModel(10_000, ambiguous);

        Assert.False(selection.IsAvailable);
        Assert.Contains("digest-bound", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectForHardware_UsesLiveAvailableGpuAndSystemMemoryBudgets()
    {
        var profile = new LocalHardwareProfile(
            DateTimeOffset.UtcNow,
            "Windows fixture",
            "X64",
            "CPU fixture",
            8,
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            1_000_000_000_000,
            500_000_000_000,
            Array.AsReadOnly<LocalGpuProfile>(
            [
                new("Small GPU", null, null, 4L * 1024 * 1024 * 1024, "fixture", 2L * 1024 * 1024 * 1024, "fixture live", true),
                new("Large GPU", null, null, 8_188L * 1024 * 1024, "fixture", 6_000_000_000, "fixture live", true),
            ]));

        var selection = new LocalModelSelector().SelectForHardware(profile, Models);

        Assert.Equal(6_000_000_000, selection.AvailableGraphicsMemoryBytes);
        Assert.Equal(profile.AvailableMemoryBytes, selection.AvailableSystemMemoryBytes);
        Assert.Equal("vision-4b:" + Vision4Digest, selection.Vision.Model?.Name);
        Assert.Equal("embed-small:" + EmbedSmallDigest, selection.Embedding.Model?.Name);
    }

    [Fact]
    public void SelectForHardware_DoesNotTreatInventoryOnlyDxgiBudgetAsComputeAdmission()
    {
        var profile = new LocalHardwareProfile(
            DateTimeOffset.UtcNow,
            "Windows fixture",
            "X64",
            "CPU fixture",
            8,
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            1_000_000_000_000,
            500_000_000_000,
            Array.AsReadOnly<LocalGpuProfile>(
            [
                new("Integrated GPU", "Intel", null, 8_000_000_000, "Windows DXGI local memory budget", 7_000_000_000, "Windows DXGI local memory budget"),
            ]));

        var selection = new LocalModelSelector().SelectForHardware(profile, Models);

        Assert.Equal(0, selection.AvailableGraphicsMemoryBytes);
        Assert.False(selection.Vision.IsAvailable);
    }

    [Fact]
    public void SelectForHardware_FailsClosedWithoutLiveGpuCapacityAndUsesAvailableRam()
    {
        var profile = new LocalHardwareProfile(
            DateTimeOffset.UtcNow,
            "Windows fixture",
            "X64",
            "CPU fixture",
            8,
            16L * 1024 * 1024 * 1024,
            512L * 1024 * 1024,
            1_000_000_000_000,
            500_000_000_000,
            Array.AsReadOnly<LocalGpuProfile>(
            [
                new("Static-only GPU", null, null, 8L * 1024 * 1024 * 1024, "registry fixture"),
            ]));

        var selection = new LocalModelSelector().SelectForHardware(profile, Models);

        Assert.Equal(0, selection.AvailableGraphicsMemoryBytes);
        Assert.False(selection.Vision.IsAvailable);
        Assert.False(selection.Embedding.IsAvailable);
        Assert.Contains("usable-VRAM", selection.Vision.Reason, StringComparison.Ordinal);
        Assert.Contains("available-RAM", selection.Embedding.Reason, StringComparison.Ordinal);
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
