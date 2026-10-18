using System.Security.Cryptography;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class OllamaRuntimeSelectionStoreTests : IDisposable
{
    private readonly MemoryLocalModelApprovalStateAnchorStore anchors = new();
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-ollama-runtime-selection-{Guid.NewGuid():N}");
    private readonly DateTimeOffset reviewedUtc =
        DateTimeOffset.Parse("2026-10-19T00:00:00Z");

    [Fact]
    public async Task Selection_RequiresExplicitSignedReviewAndSurvivesProtectedReopenUntilCleared()
    {
        var fixture = await CreateReviewAsync();
        var store = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);

        Assert.Null((await store.GetStateAsync()).Runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: false,
            reviewedUtc.AddMinutes(1)));

        await store.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1));
        var reopened = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);
        var approved = Assert.IsType<ApprovedOllamaRuntimeConfiguration>(
            (await reopened.GetStateAsync()).Runtime);

        Assert.Equal(fixture.Review.Binding.TreeSha256, approved.Runtime.TreeSha256);
        Assert.Equal(fixture.Review.Binding.SignerCertificateSha256, approved.Runtime.SignerCertificateSha256);
        Assert.Equal(fixture.ModelRoot, approved.ModelRoot);
        Assert.Equal(reviewedUtc.AddMinutes(1), approved.ApprovedUtc);
        Assert.False(reopened.RequiresReapproval);

        await reopened.ClearAsync();
        Assert.Null((await (await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors))
            .GetStateAsync()).Runtime);
    }

    [Fact]
    public async Task Selection_RejectsUnsignedChangedLicenseAndUnsafeModelRoot()
    {
        var fixture = await CreateReviewAsync();
        var store = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);
        var unsigned = fixture.Review with
        {
            Binding = fixture.Review.Binding with
            {
                AuthenticodeStatus = "NotSigned",
                SignerSubject = null,
                SignerCertificateSha256 = null,
            },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            unsigned,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            fixture.Review with { OfficialLicenseUri = "https://example.invalid/license" },
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1)));

        var outside = Path.Combine(root, "outside-models");
        var linked = Path.Combine(root, "linked-models");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linked, outside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            fixture.Review,
            linked,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1)));

        Assert.Null((await store.GetStateAsync()).Runtime);
    }

    [Fact]
    public async Task ProtectedApproval_RejectsRollbackMissingAnchorAndAllowsExactReapproval()
    {
        var fixture = await CreateReviewAsync();
        var store = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);
        await store.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1));
        var statePath = Path.Combine(root, "local-models", "ollama-runtime.json");
        var firstState = await File.ReadAllBytesAsync(statePath);
        await store.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(2));

        await File.WriteAllBytesAsync(statePath, firstState);
        var replayed = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);
        Assert.True(replayed.RequiresReapproval);
        Assert.Null((await replayed.GetStateAsync()).Runtime);

        await replayed.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(3));
        Assert.False(replayed.RequiresReapproval);
        Assert.Equal(
            reviewedUtc.AddMinutes(3),
            (await replayed.GetStateAsync()).Runtime?.ApprovedUtc);

        anchors.RemoveAll();
        var missing = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(root, anchors);
        Assert.True(missing.RequiresReapproval);
        Assert.Null((await missing.GetStateAsync()).Runtime);
    }

    [Fact]
    public async Task Store_RejectsLinkedStateAndSettingsSwapWithoutWritingOutside()
    {
        var settings = Path.Combine(root, "linked-state", "local-models");
        Directory.CreateDirectory(settings);
        var outsideState = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outsideState, "{}");
        File.CreateSymbolicLink(Path.Combine(settings, "ollama-runtime.json"), outsideState);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OllamaRuntimeSelectionStore.OpenWithAnchorAsync(
                Path.Combine(root, "linked-state"),
                anchors));
        Assert.Equal("{}", await File.ReadAllTextAsync(outsideState));

        var dataRoot = Path.Combine(root, "swapped");
        var store = await OllamaRuntimeSelectionStore.OpenWithAnchorAsync(dataRoot, anchors);
        var fixture = await CreateReviewAsync("second-runtime", "second-models");
        var localModels = Path.Combine(dataRoot, "local-models");
        Directory.Move(localModels, localModels + "-original");
        var outside = Path.Combine(root, "outside-settings");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(localModels, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            fixture.Review,
            fixture.ModelRoot,
            explicitlyApproved: true,
            reviewedUtc.AddMinutes(1)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<Fixture> CreateReviewAsync(
        string runtimeName = "runtime",
        string modelName = "models")
    {
        var runtimeRoot = Path.Combine(root, runtimeName);
        var modelRoot = Path.Combine(root, modelName);
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(modelRoot);
        var executable = Path.Combine(runtimeRoot, "ollama.exe");
        await File.WriteAllTextAsync(executable, "signed fixture executable");
        var length = new FileInfo(executable).Length;
        var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable)));
        IReadOnlyList<OllamaRuntimeFileBinding> files =
        [
            new("ollama.exe", length, digest),
        ];
        var binding = new ApprovedOllamaRuntimeBinding(
            runtimeRoot,
            OllamaRuntimeReviewer.CompleteTreeProfile,
            "ollama.exe",
            files,
            OllamaRuntimeReviewer.ComputeTreeDigest(
                OllamaRuntimeReviewer.CompleteTreeProfile,
                "ollama.exe",
                files),
            "Valid",
            "CN=Fixture Ollama",
            new string('a', 64),
            reviewedUtc);
        return new Fixture(
            new OllamaRuntimeReview(
                binding,
                length,
                OllamaRuntimeReviewer.OfficialLicenseUri),
            modelRoot);
    }

    private sealed record Fixture(OllamaRuntimeReview Review, string ModelRoot);
}
