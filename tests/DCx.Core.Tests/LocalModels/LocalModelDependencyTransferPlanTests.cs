using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelDependencyTransferPlanTests
{
    [Fact]
    public async Task EmbeddedCatalog_ProducesExactDeduplicatedTransferAndStorageFootprint()
    {
        var catalog = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();
        var plan = new LocalModelDependencyTransferPlanner().CreateForAll(catalog, 3_098_988_452);

        Assert.Equal(LocalModelDependencyTransferPlanner.CurrentSchemaVersion, plan.SchemaVersion);
        Assert.Equal(LocalModelDependencyCatalogLoader.EmbeddedCatalogId, plan.CatalogId);
        Assert.Equal(LocalModelDependencyCatalogLoader.EmbeddedCatalogSha256, plan.CatalogSha256);
        Assert.Equal(4, plan.CandidateIds.Count);
        Assert.Equal(9, plan.Items.Count);
        Assert.Equal(1_281_058_770, plan.TransferBytes);
        Assert.Equal(3_098_988_452, plan.MinimumFreeBytes);
        Assert.True(plan.CapacitySufficient);
        Assert.Equal("a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286", plan.PlanSha256);

        var sharedWhisperLicense = Assert.Single(plan.Items, item =>
            item.SourceUri.AbsoluteUri == "https://raw.githubusercontent.com/openai/whisper/v20250625/LICENSE");
        Assert.Equal(2, sharedWhisperLicense.Roles.Count);
        Assert.Contains("license/whisper-model-base-multilingual", sharedWhisperLicense.Roles);
        Assert.Contains("license/whisper-model-large-v3-turbo-q5-0", sharedWhisperLicense.Roles);

        var ollamaLicense = Assert.Single(plan.Items, item =>
            item.Sha256 == "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Assert.Equal(2, ollamaLicense.Roles.Count);
        Assert.Equal(
            "https://registry.ollama.ai/v2/library/nomic-embed-text/blobs/sha256:c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4",
            ollamaLicense.SourceUri.AbsoluteUri);
    }

    [Fact]
    public async Task Plan_IsOrderStableAndCapacityObservationDoesNotChangeAuthorityDigest()
    {
        var catalog = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();
        var ids = catalog.Candidates.Select(candidate => candidate.Id).ToArray();
        var planner = new LocalModelDependencyTransferPlanner();

        var first = planner.Create(catalog, ids, null);
        var second = planner.Create(catalog, ids.Reverse(), long.MaxValue);

        Assert.Equal(first.PlanSha256, second.PlanSha256);
        Assert.Equal(first.TransferBytes, second.TransferBytes);
        Assert.Equal(first.MinimumFreeBytes, second.MinimumFreeBytes);
        Assert.False(first.CapacitySufficient);
        Assert.True(second.CapacitySufficient);
        Assert.Null(first.ObservedAvailableFreeBytes);
    }

    [Fact]
    public async Task Plan_BindsExactSelectedCandidatesAndChangesForAValidSubset()
    {
        var catalog = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();
        var planner = new LocalModelDependencyTransferPlanner();
        var baseOnly = planner.Create(
            catalog,
            ["whisper-cuda-runtime-b5454", "whisper-model-base-multilingual"],
            1_500_000_000);
        var largeOnly = planner.Create(
            catalog,
            ["whisper-cuda-runtime-b5454", "whisper-model-large-v3-turbo-q5-0"],
            1_500_000_000);

        Assert.Equal(4, baseOnly.Items.Count);
        Assert.NotEqual(baseOnly.PlanSha256, largeOnly.PlanSha256);
        Assert.NotEqual(baseOnly.TransferBytes, largeOnly.TransferBytes);
        Assert.DoesNotContain(baseOnly.Items, item =>
            item.SourceUri.AbsoluteUri.Contains("ggml-large-v3-turbo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_RejectsUnknownDuplicateOrMalformedSelectionsAndCatalogs()
    {
        var catalog = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();
        var planner = new LocalModelDependencyTransferPlanner();

        Assert.Throws<InvalidOperationException>(() => planner.Create(catalog, [], 1));
        Assert.Throws<InvalidOperationException>(() => planner.Create(catalog, ["unknown"], 1));
        Assert.Throws<InvalidOperationException>(() => planner.Create(
            catalog,
            [catalog.Candidates[0].Id, catalog.Candidates[0].Id],
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => planner.CreateForAll(catalog, -1));

        var duplicateCatalog = catalog with
        {
            Candidates = Array.AsReadOnly([catalog.Candidates[0], catalog.Candidates[0]]),
        };
        Assert.Throws<InvalidOperationException>(() => planner.CreateForAll(duplicateCatalog, 1));
        Assert.Throws<InvalidOperationException>(() => planner.CreateForAll(
            catalog with { CatalogSha256 = new string('g', 64) },
            1));
    }

    [Fact]
    public async Task Plan_RejectsConflictingIdentityForOneExactSource()
    {
        var catalog = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();
        var first = catalog.Candidates[1];
        var conflict = catalog.Candidates[2] with
        {
            Id = "conflicting-source-candidate",
            SourceUri = first.SourceUri,
        };
        var conflictingCatalog = catalog with
        {
            Candidates = Array.AsReadOnly([first, conflict]),
        };

        Assert.Throws<InvalidOperationException>(() =>
            new LocalModelDependencyTransferPlanner().CreateForAll(conflictingCatalog, long.MaxValue));
    }
}
