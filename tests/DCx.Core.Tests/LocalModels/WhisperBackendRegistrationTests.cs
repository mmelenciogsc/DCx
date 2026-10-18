using System.Security.Cryptography;
using System.Text;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class WhisperBackendRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-whisper-registration-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task ReviewAndApprovedBinding_ExposeFullLicensesAndTranscribeWithPinnedFixture()
    {
        var fixture = await CreateFixtureAsync();
        var review = await new WhisperBackendReviewer().ReviewAsync(
            fixture.Executable,
            fixture.Model,
            fixture.RuntimeLicense,
            fixture.ModelLicense,
            now);

        Assert.Equal("Fixture runtime license.\nSecond line.", review.RuntimeLicenseText);
        Assert.Equal("Fixture model license.\nAll local.", review.ModelLicenseText);
        Assert.Equal(new FileInfo(fixture.Executable).Length, review.ExecutableBytes);
        Assert.Equal(new FileInfo(fixture.Model).Length, review.ModelBytes);
        Assert.Equal(await FileSha256Async(fixture.Executable), review.Binding.ExecutableSha256);
        Assert.Equal(await FileSha256Async(fixture.Model), review.Binding.ModelSha256);
        Assert.Equal(await FileSha256Async(fixture.RuntimeLicense), review.Binding.RuntimeLicenseSha256);
        Assert.Equal(await FileSha256Async(fixture.ModelLicense), review.Binding.ModelLicenseSha256);

        var result = await review.Binding
            .CreateTranscriber(TimeSpan.FromSeconds(5))
            .TranscribeWavAsync(fixture.Audio);

        Assert.Equal("DCx transcription fixture 42.", result.Text);
    }

    [Fact]
    public async Task Store_RequiresExplicitApprovalPersistsAtomicallyAndClearsWithoutDeletingSources()
    {
        var fixture = await CreateFixtureAsync();
        var review = await ReviewAsync(fixture);
        var dataRoot = Path.Combine(root, "data");
        var store = await WhisperBackendSelectionStore.OpenAsync(dataRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(review.Binding, false));
        await store.SetAsync(review.Binding, true);

        var reopened = await WhisperBackendSelectionStore.OpenAsync(dataRoot);
        var stored = Assert.IsType<ApprovedWhisperBackendBinding>((await reopened.GetStateAsync()).Backend);
        Assert.True(review.Binding.HasSameIdentity(stored));
        Assert.Equal(now, stored.ApprovedUtc);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dataRoot, "local-models"), "*.tmp"));

        await reopened.ClearAsync();
        Assert.Null((await (await WhisperBackendSelectionStore.OpenAsync(dataRoot)).GetStateAsync()).Backend);
        Assert.All(
            new[] { fixture.Executable, fixture.Model, fixture.RuntimeLicense, fixture.ModelLicense },
            path => Assert.True(File.Exists(path), $"The source file was unexpectedly removed: {path}"));
    }

    [Fact]
    public async Task Rereview_DetectsChangedModelAndLicenseBeforeApproval()
    {
        var fixture = await CreateFixtureAsync();
        var first = await ReviewAsync(fixture);
        await File.WriteAllTextAsync(fixture.Model, "changed model bytes");
        await File.WriteAllTextAsync(fixture.ModelLicense, "Changed model license.");

        var second = await ReviewAsync(fixture);

        Assert.False(first.Binding.HasSameIdentity(second.Binding));
        Assert.NotEqual(first.Binding.ModelSha256, second.Binding.ModelSha256);
        Assert.NotEqual(first.Binding.ModelLicenseSha256, second.Binding.ModelLicenseSha256);
    }

    [Fact]
    public async Task ApprovedBinding_RejectsLicenseByteChangesBeforeCreatingTranscriber()
    {
        var fixture = await CreateFixtureAsync();
        var review = await ReviewAsync(fixture);
        await File.AppendAllTextAsync(fixture.RuntimeLicense, "\n");

        Assert.Throws<InvalidOperationException>(() =>
            review.Binding.CreateTranscriber(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"backend\":null}")]
    [InlineData("{\"schemaVersion\":1,\"backend\":null,\"unknown\":true}")]
    [InlineData("not json")]
    public async Task PersistedSelection_FailsClosedOnFutureUnknownOrMalformedState(string json)
    {
        var localModels = Path.Combine(root, "data", "local-models");
        Directory.CreateDirectory(localModels);
        await File.WriteAllTextAsync(Path.Combine(localModels, "transcription.json"), json);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhisperBackendSelectionStore.OpenAsync(Path.Combine(root, "data")));
    }

    [Fact]
    public async Task PersistedSelection_RejectsUnsupportedLicensePathAndDuplicateComponents()
    {
        var fixture = await CreateFixtureAsync();
        var review = await ReviewAsync(fixture);
        var binding = review.Binding with
        {
            RuntimeLicensePath = fixture.Executable,
            RuntimeLicenseSha256 = review.Binding.ExecutableSha256,
        };
        var dataRoot = Path.Combine(root, "data");
        var localModels = Path.Combine(dataRoot, "local-models");
        Directory.CreateDirectory(localModels);
        var state = System.Text.Json.JsonSerializer.Serialize(
            new WhisperBackendSelectionState(WhisperBackendSelectionStore.CurrentSchemaVersion, binding),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(Path.Combine(localModels, "transcription.json"), state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WhisperBackendSelectionStore.OpenAsync(dataRoot));
    }

    [Fact]
    public async Task Store_RejectsNetworkBackendPaths()
    {
        var fixture = await CreateFixtureAsync();
        var review = await ReviewAsync(fixture);
        var networkBinding = review.Binding with
        {
            ExecutablePath = @"\\server\share\whisper.exe",
        };
        var store = await WhisperBackendSelectionStore.OpenAsync(Path.Combine(root, "data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(networkBinding, true));
    }

    [Fact]
    public async Task Review_RejectsBinaryEmptyOrUnsupportedLicenseAndExecutablePaths()
    {
        var fixture = await CreateFixtureAsync();
        await File.WriteAllBytesAsync(fixture.RuntimeLicense, [0xff, 0xfe, 0xfd]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewAsync(fixture));

        await File.WriteAllTextAsync(fixture.RuntimeLicense, string.Empty);
        await Assert.ThrowsAsync<ArgumentException>(() => ReviewAsync(fixture));

        var unsupported = Path.Combine(root, "unsupported-whisper.cmd");
        File.Copy(fixture.Executable, unsupported);
        await Assert.ThrowsAsync<ArgumentException>(() => new WhisperBackendReviewer().ReviewAsync(
            unsupported,
            fixture.Model,
            fixture.RuntimeLicense,
            fixture.ModelLicense,
            now));
    }

    [Fact]
    public async Task Transcriber_RejectsResourceLimitsBeforeInspectingFiles()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(
            new WhisperCliTranscriber(new WhisperCliConfiguration(
                Path.Combine(root, "missing.exe"),
                new string('1', 64),
                Path.Combine(root, "missing.bin"),
                new string('2', 64),
                TimeSpan.FromSeconds(1),
                MaximumExecutableBytes: 0))));
    }

    [Fact]
    public async Task Store_RejectsSettingsDirectorySwappedForLinkAfterOpen()
    {
        var fixture = await CreateFixtureAsync();
        var review = await ReviewAsync(fixture);
        var dataRoot = Path.Combine(root, "data");
        var store = await WhisperBackendSelectionStore.OpenAsync(dataRoot);
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
        File.CreateSymbolicLink(Path.Combine(settings, "transcription.json"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhisperBackendSelectionStore.OpenAsync(dataRoot));

        Assert.Equal("{}", await File.ReadAllTextAsync(outside));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(root);
        var model = Path.Combine(root, "fixture-model.bin");
        var runtimeLicense = Path.Combine(root, "runtime-license.txt");
        var modelLicense = Path.Combine(root, "model-license.md");
        var audio = Path.Combine(root, "fixture.wav");
        await File.WriteAllTextAsync(model, "fixture whisper model");
        await File.WriteAllTextAsync(runtimeLicense, "\ufeff  Fixture runtime license.\r\nSecond line.  \r\n");
        await File.WriteAllTextAsync(modelLicense, "Fixture model license.\nAll local.\n");
        await File.WriteAllBytesAsync(audio, WaveBytes());
        return new Fixture(FixtureExecutable(), model, runtimeLicense, modelLicense, audio);
    }

    private Task<WhisperBackendReview> ReviewAsync(Fixture fixture) =>
        new WhisperBackendReviewer().ReviewAsync(
            fixture.Executable,
            fixture.Model,
            fixture.RuntimeLicense,
            fixture.ModelLicense,
            now);

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static byte[] WaveBytes() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E',
    ];

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(
            directory.FullName,
            "tests",
            "DCx.FakeWhisperCli",
            "bin",
            "Release",
            "net10.0",
            "DCx.FakeWhisperCli.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException("Build the fake Whisper CLI fixture before running tests.", executable);
    }

    private sealed record Fixture(
        string Executable,
        string Model,
        string RuntimeLicense,
        string ModelLicense,
        string Audio);
}
