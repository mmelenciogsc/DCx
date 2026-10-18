using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelSelectionStoreTests : IDisposable
{
    private readonly MemoryLocalModelApprovalStateAnchorStore anchors = new();
    private const string VisionDigest = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string EmbeddingDigest = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string LicenseDigest = "3333333333333333333333333333333333333333333333333333333333333333";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-local-models-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task VisionSelection_RequiresApprovalAndSurvivesReopenUntilCleared()
    {
        var descriptor = new LocalModelDescriptor(
            "vision:" + VisionDigest, 1_000, true, false, Digest: VisionDigest);
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        Assert.Null((await store.GetStateAsync()).Vision);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetVisionAsync(descriptor, LicenseDigest, false, now));

        await store.SetVisionAsync(descriptor, LicenseDigest, true, now);
        var reopened = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        var binding = Assert.IsType<ApprovedLocalModelBinding>((await reopened.GetStateAsync()).Vision);
        Assert.Equal(descriptor.Name, binding.Name);
        Assert.Equal(VisionDigest, binding.Digest);
        Assert.Equal(LicenseDigest, binding.LicenseSha256);
        Assert.Equal(now, binding.ApprovedUtc);

        await reopened.ClearVisionAsync();
        var cleared = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        Assert.Null((await cleared.GetStateAsync()).Vision);
    }

    [Fact]
    public async Task Selections_RejectMutableNamesWrongCapabilitiesAndInvalidLicenseDigest()
    {
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        var mutable = new LocalModelDescriptor("friendly-tag", 1_000, true, false, Digest: VisionDigest);
        var embeddingOnly = new LocalModelDescriptor(
            "embedding:" + EmbeddingDigest, 1_000, false, true, Digest: EmbeddingDigest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetVisionAsync(mutable, LicenseDigest, true, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetVisionAsync(embeddingOnly, LicenseDigest, true, now));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetEmbeddingAsync(embeddingOnly, "bad", true, now));
        Assert.Equal(new LocalModelSelectionState(1, null, null), await store.GetStateAsync());
    }

    [Fact]
    public async Task VisionAndEmbeddingSelections_AreIndependentAndAtomicallyPersisted()
    {
        var vision = new LocalModelDescriptor(
            "vision:" + VisionDigest, 1_000, true, false, Digest: VisionDigest);
        var embedding = new LocalModelDescriptor(
            "embedding:" + EmbeddingDigest, 500, false, true, Digest: EmbeddingDigest);
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);

        await store.SetVisionAsync(vision, LicenseDigest, true, now);
        await store.SetEmbeddingAsync(embedding, LicenseDigest, true, now.AddMinutes(1));
        await store.ClearEmbeddingAsync();

        var state = await (await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors)).GetStateAsync();
        Assert.NotNull(state.Vision);
        Assert.Null(state.Embedding);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "local-models"), "*.tmp"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"vision\":null,\"embedding\":null}")]
    [InlineData("{\"schemaVersion\":1,\"vision\":null,\"embedding\":null,\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"vision\":{\"name\":\"mutable\",\"digest\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"licenseSha256\":\"3333333333333333333333333333333333333333333333333333333333333333\",\"approvedUtc\":\"2026-10-17T00:00:00Z\"},\"embedding\":null}")]
    public async Task PersistedSelection_FailsClosedOnFutureUnknownOrMutableState(string json)
    {
        var directory = Path.Combine(root, "local-models");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "selection.json"), json);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors));
    }

    [Fact]
    public async Task PersistedSelection_FailsClosedWhenStatePathIsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(root, "local-models", "selection.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors));
    }

    [Fact]
    public async Task Store_RejectsLinkedApplicationRootWithoutWritingThroughIt()
    {
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside");
        var linked = Path.Combine(root, "linked-data");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linked, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelSelectionStore.OpenWithAnchorAsync(linked, anchors));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Store_RejectsSettingsDirectorySwappedForLinkAfterOpen()
    {
        var dataRoot = Path.Combine(root, "data");
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(dataRoot, anchors);
        var settings = Path.Combine(dataRoot, "local-models");
        Directory.Move(settings, settings + "-original");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(settings, outside);
        var descriptor = new LocalModelDescriptor(
            "vision:" + VisionDigest, 1_000, true, false, Digest: VisionDigest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetVisionAsync(descriptor, LicenseDigest, true, now));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Store_RejectsLinkedSelectionFile()
    {
        var settings = Path.Combine(root, "local-models");
        Directory.CreateDirectory(settings);
        var outside = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");
        File.CreateSymbolicLink(Path.Combine(settings, "selection.json"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors));

        Assert.Equal("{}", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ProtectedApproval_RejectsRollbackAndAllowsExplicitReapproval()
    {
        var descriptor = new LocalModelDescriptor(
            "vision:" + VisionDigest, 1_000, true, false, Digest: VisionDigest);
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        await store.SetVisionAsync(descriptor, LicenseDigest, true, now);
        var statePath = Path.Combine(root, "local-models", "selection.json");
        var firstState = await File.ReadAllBytesAsync(statePath);
        await store.SetVisionAsync(descriptor, LicenseDigest, true, now.AddMinutes(1));

        await File.WriteAllBytesAsync(statePath, firstState);
        var replayed = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);

        Assert.True(replayed.RequiresReapproval);
        Assert.Null((await replayed.GetStateAsync()).Vision);

        await replayed.SetVisionAsync(descriptor, LicenseDigest, true, now.AddMinutes(2));
        Assert.False(replayed.RequiresReapproval);
        Assert.Equal(now.AddMinutes(2), (await replayed.GetStateAsync()).Vision?.ApprovedUtc);
    }

    [Fact]
    public async Task ProtectedApproval_MissingOrUnreadableAnchorFailsClosedAndWriteFailureRecovers()
    {
        var descriptor = new LocalModelDescriptor(
            "vision:" + VisionDigest, 1_000, true, false, Digest: VisionDigest);
        var store = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        await store.SetVisionAsync(descriptor, LicenseDigest, true, now);

        anchors.RemoveAll();
        var missing = await LocalModelSelectionStore.OpenWithAnchorAsync(root, anchors);
        Assert.True(missing.RequiresReapproval);
        Assert.Null((await missing.GetStateAsync()).Vision);

        anchors.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missing.SetVisionAsync(descriptor, LicenseDigest, true, now.AddMinutes(1)));
        Assert.True((await missing.GetStateAsync()).Vision is null);
        Assert.True(missing.RequiresReapproval);

        anchors.FailWrites = false;
        await missing.SetVisionAsync(descriptor, LicenseDigest, true, now.AddMinutes(2));
        anchors.FailReads = true;
        Assert.Null((await missing.GetStateAsync()).Vision);
        Assert.True(missing.RequiresReapproval);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);
}
