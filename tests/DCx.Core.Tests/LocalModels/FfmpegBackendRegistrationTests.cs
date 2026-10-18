using System.Security.Cryptography;
using DCx.Core.Deployment;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class FfmpegBackendRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-ffmpeg-registration-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task Review_BindsExactExecutableLicenseAndAuthenticodeEvidence()
    {
        var fixture = await CreateFixtureAsync();
        var review = await new FfmpegBackendReviewer(new SignatureProbe("NotSigned", null, null))
            .ReviewAsync(fixture.Executable, fixture.License, now);

        Assert.Equal("Fixture GPL-compatible license.\nComplete text.", review.LicenseText);
        Assert.Equal(new FileInfo(fixture.Executable).Length, review.ExecutableBytes);
        Assert.Equal(await DigestAsync(fixture.Executable), review.Binding.ExecutableSha256);
        Assert.Equal(await DigestAsync(fixture.License), review.Binding.LicenseSha256);
        Assert.Equal("NotSigned", review.Binding.AuthenticodeStatus);
        Assert.Null(review.Binding.SignerSubject);
        Assert.Equal(now, review.Binding.ApprovedUtc);
    }

    [Fact]
    public async Task Store_RequiresApprovalPersistsAndClearsWithoutDeletingFiles()
    {
        var fixture = await CreateFixtureAsync();
        var review = await new FfmpegBackendReviewer(new SignatureProbe("NotSigned", null, null))
            .ReviewAsync(fixture.Executable, fixture.License, now);
        var dataRoot = Path.Combine(root, "data");
        var store = await FfmpegBackendSelectionStore.OpenAsync(dataRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(review.Binding, false));
        await store.SetAsync(review.Binding, true);
        var reopened = await FfmpegBackendSelectionStore.OpenAsync(dataRoot);
        var stored = Assert.IsType<ApprovedFfmpegBinding>((await reopened.GetStateAsync()).Backend);
        Assert.True(review.Binding.HasSameIdentity(stored));

        await reopened.ClearAsync();
        Assert.Null((await reopened.GetStateAsync()).Backend);
        Assert.True(File.Exists(fixture.Executable));
        Assert.True(File.Exists(fixture.License));
    }

    [Fact]
    public async Task ReviewAndStore_AcceptCompleteValidSignerEvidence()
    {
        var fixture = await CreateFixtureAsync();
        var certificate = new string('a', 64);
        var review = await new FfmpegBackendReviewer(new SignatureProbe("Valid", "CN=Fixture", certificate))
            .ReviewAsync(fixture.Executable, fixture.License, now);
        var store = await FfmpegBackendSelectionStore.OpenAsync(Path.Combine(root, "data"));

        await store.SetAsync(review.Binding, true);

        var stored = Assert.IsType<ApprovedFfmpegBinding>((await store.GetStateAsync()).Backend);
        Assert.Equal("CN=Fixture", stored.SignerSubject);
        Assert.Equal(certificate, stored.SignerCertificateSha256);
    }

    [Fact]
    public async Task Store_RejectsNetworkAndInconsistentSignerEvidence()
    {
        var fixture = await CreateFixtureAsync();
        var review = await new FfmpegBackendReviewer(new SignatureProbe("NotSigned", null, null))
            .ReviewAsync(fixture.Executable, fixture.License, now);
        var store = await FfmpegBackendSelectionStore.OpenAsync(Path.Combine(root, "data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            review.Binding with { ExecutablePath = @"\\server\share\ffmpeg.exe" }, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            review.Binding with { SignerSubject = "CN=Unexpected" }, true));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"backend\":null}")]
    [InlineData("{\"schemaVersion\":1,\"backend\":null,\"unknown\":true}")]
    [InlineData("not json")]
    public async Task PersistedState_FailsClosedOnFutureUnknownOrMalformedContent(string json)
    {
        var directory = Path.Combine(root, "data", "local-models");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "ffmpeg.json"), json);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FfmpegBackendSelectionStore.OpenAsync(Path.Combine(root, "data")));
    }

    [Fact]
    public async Task Store_RejectsSettingsDirectorySwappedForLinkAfterOpen()
    {
        var fixture = await CreateFixtureAsync();
        var review = await new FfmpegBackendReviewer(new SignatureProbe("NotSigned", null, null))
            .ReviewAsync(fixture.Executable, fixture.License, now);
        var dataRoot = Path.Combine(root, "data");
        var store = await FfmpegBackendSelectionStore.OpenAsync(dataRoot);
        var settings = Path.Combine(dataRoot, "local-models");
        Directory.Move(settings, settings + "-original");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(settings, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(review.Binding, true));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Store_RejectsLinkedSelectionFile()
    {
        var dataRoot = Path.Combine(root, "data");
        var settings = Path.Combine(dataRoot, "local-models");
        Directory.CreateDirectory(settings);
        var outside = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");
        File.CreateSymbolicLink(Path.Combine(settings, "ffmpeg.json"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FfmpegBackendSelectionStore.OpenAsync(dataRoot));

        Assert.Equal("{}", await File.ReadAllTextAsync(outside));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(root);
        var license = Path.Combine(root, "LICENSE");
        await File.WriteAllTextAsync(license, "\ufeff Fixture GPL-compatible license.\r\nComplete text. \r\n");
        return new(FixtureExecutable(), license);
    }

    private static async Task<string> DigestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(directory.FullName, "tests", "DCx.FakeWhisperCli", "bin", "Release", "net10.0", "DCx.FakeWhisperCli.exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException("Build the fixture first.", executable);
    }

    private sealed record Fixture(string Executable, string License);

    private sealed class SignatureProbe(string status, string? subject, string? certificate) : IPackageAuthenticodeProbe
    {
        public Task<PackageAuthenticodeEvidence> InspectAsync(string packagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PackageAuthenticodeEvidence(status, subject, certificate));
        }
    }
}
