using DCx.Core.Contributions;
using DCx.Core.Knowledge;

namespace DCx.Core.Tests.Contributions;

public sealed class ContributionArtifactSelectionServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-contribution-artifact-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadCcb_ValidatesClosedPackageWithoutRetainingPathAndDisposeZeroesBytes()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "reviewed-tool.zip");
        var packageBytes = CcbContributionPackageTests.CreatePackage();
        await File.WriteAllBytesAsync(path, packageBytes);
        var service = new ContributionArtifactSelectionService();

        using var selection = await service.LoadCcbAsync(path);
        var retainedView = selection.Content;

        Assert.Equal("reviewed-tool.zip", selection.FileName);
        Assert.DoesNotContain(root, selection.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(packageBytes.Length, selection.SizeBytes);
        Assert.Equal(64, selection.Sha256.Length);
        Assert.Equal("tool:community-upper", selection.Manifest.Id);
        Assert.Equal("1.0.0", selection.Manifest.Version);
        Assert.Contains("MIT", selection.DeclaredLicenses);
        Assert.Contains("MIT License", selection.ReviewText, StringComparison.Ordinal);
        selection.Dispose();
        Assert.All(retainedView.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => selection.Content.ToArray());
    }

    [Fact]
    public async Task LoadCcb_RejectsMissingDirectoryOversizeLinkAndMalformedPackageWithStableCodes()
    {
        Directory.CreateDirectory(root);
        var service = new ContributionArtifactSelectionService();
        await AssertCodeAsync(
            () => service.LoadCcbAsync(Path.Combine(root, "missing.zip")),
            "ccb_artifact_unsafe");
        await AssertCodeAsync(() => service.LoadCcbAsync(root), "ccb_artifact_unsafe");

        var oversized = Path.Combine(root, "oversized.zip");
        await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ContentAddressedArtifactStore.MaximumArtifactBytes + 1L);
        }
        await AssertCodeAsync(() => service.LoadCcbAsync(oversized), "ccb_artifact_unsafe");

        var malformed = Path.Combine(root, "malformed.zip");
        await File.WriteAllBytesAsync(malformed, "not a package"u8.ToArray());
        await AssertCodeAsync(() => service.LoadCcbAsync(malformed), "ccb_artifact_policy_invalid");

        var target = Path.Combine(root, "target.zip");
        await File.WriteAllBytesAsync(target, CcbContributionPackageTests.CreatePackage());
        var link = Path.Combine(root, "linked.zip");
        File.CreateSymbolicLink(link, target);
        await AssertCodeAsync(() => service.LoadCcbAsync(link), "ccb_artifact_unsafe");
    }

    [Fact]
    public async Task LoadCcb_HonorsCancellation()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "canceled.zip");
        await File.WriteAllBytesAsync(path, CcbContributionPackageTests.CreatePackage());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ContributionArtifactSelectionService().LoadCcbAsync(path, cancellation.Token));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static async Task AssertCodeAsync(
        Func<Task<SelectedContributionArtifact>> action,
        string expectedCode)
    {
        var exception = await Assert.ThrowsAsync<ContributionArtifactException>(action);
        Assert.Equal(expectedCode, exception.Code);
    }
}
