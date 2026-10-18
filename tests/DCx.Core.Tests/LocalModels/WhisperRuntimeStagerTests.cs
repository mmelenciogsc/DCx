using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class WhisperRuntimeStagerTests : IDisposable
{
    private static readonly DateTimeOffset StagedUtc =
        new(2026, 10, 18, 8, 15, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-whisper-runtime-stager-{Guid.NewGuid():N}");

    [Fact]
    public async Task Stage_DeniesWithoutExplicitApprovalAndCreatesNothing()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager().StageAsync(
                RuntimeCandidate(bytes),
                source,
                applicationRoot,
                explicitlyApproved: false));

        Assert.False(Directory.Exists(applicationRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Stage_AuthenticatesExtractsHashesAndAtomicallyPublishesInertRuntime()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "selected-runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(bytes);
        var applicationRoot = Path.Combine(root, "application");

        var result = await new WhisperRuntimeStager(
            new FixedTimeProvider(StagedUtc)).StageAsync(
                candidate,
                source,
                applicationRoot,
                explicitlyApproved: true);

        Assert.Equal(candidate.Id, result.CandidateId);
        Assert.Equal(candidate.Sha256, result.ArchiveSha256);
        Assert.Equal(StagedUtc, result.StagedUtc);
        Assert.EndsWith(candidate.Id + "-" + candidate.Sha256, result.RuntimeDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(result.RuntimeDirectory, "Release", "whisper-cli.exe"), result.WhisperCliPath);
        Assert.Equal(Path.Combine(result.RuntimeDirectory, WhisperRuntimeStager.ReceiptFileName), result.ReceiptPath);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(1, result.DirectoryCount);
        Assert.Equal(Encoding.UTF8.GetByteCount("MIT fixture license") + 8, result.ExpandedBytes);
        Assert.Equal(["LICENSE"], result.LicenseNoticeEntries);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(result.WhisperCliPath));
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(64, file.Sha256.Length));

        var receiptText = await File.ReadAllTextAsync(result.ReceiptPath);
        Assert.DoesNotContain(source, receiptText, StringComparison.OrdinalIgnoreCase);
        using var receipt = JsonDocument.Parse(receiptText);
        Assert.Equal(WhisperRuntimeStager.CurrentReceiptSchemaVersion,
            receipt.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(candidate.Sha256, receipt.RootElement.GetProperty("archiveSha256").GetString());
        Assert.Equal("Release/whisper-cli.exe",
            receipt.RootElement.GetProperty("whisperCliRelativePath").GetString());
        Assert.Equal(3, receipt.RootElement.GetProperty("files").GetArrayLength());

        var runtimeRoot = Path.GetDirectoryName(result.RuntimeDirectory)!;
        Assert.Single(Directory.EnumerateFileSystemEntries(runtimeRoot));
        Assert.Equal(4, Directory.EnumerateFiles(
            result.RuntimeDirectory, "*", SearchOption.AllDirectories).Count());
        Assert.False(File.Exists(Path.Combine(applicationRoot, "local-models", "whisper.json")));
    }

    [Fact]
    public async Task Stage_DeniesUnsafeArchiveAndRemovesOnlyPrivatePartialState()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes(
            new RuntimeEntry("../whisper-cli.exe", [1]),
            new RuntimeEntry("LICENSE", [2]));
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager().StageAsync(
                RuntimeCandidate(bytes), source, applicationRoot, explicitlyApproved: true));

        var runtimeRoot = Path.Combine(applicationRoot, "local-models", "whisper-runtime");
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtimeRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Stage_DeniesReceiptCollisionAndLeavesNoPublishedOrPrivateState()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes(
            new RuntimeEntry("Release/whisper-cli.exe", [1, 2, 3, 4]),
            new RuntimeEntry(WhisperRuntimeStager.ReceiptFileName, [5]));
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager().StageAsync(
                RuntimeCandidate(bytes), source, applicationRoot, explicitlyApproved: true));

        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(applicationRoot, "local-models", "whisper-runtime")));
    }

    [Fact]
    public async Task Stage_NeverOverwritesExistingDigestBoundDestination()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(bytes);
        var applicationRoot = Path.Combine(root, "application");
        var runtimeRoot = Path.Combine(applicationRoot, "local-models", "whisper-runtime");
        var destination = Path.Combine(runtimeRoot, candidate.Id + "-" + candidate.Sha256);
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve me");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager().StageAsync(
                candidate, source, applicationRoot, explicitlyApproved: true));

        Assert.Equal("preserve me", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(runtimeRoot));
    }

    [Fact]
    public async Task Stage_DeniesLinkedRuntimeRootWithoutWritingOutside()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");
        var localModels = Path.Combine(applicationRoot, "local-models");
        Directory.CreateDirectory(localModels);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(localModels, "whisper-runtime"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager().StageAsync(
                RuntimeCandidate(bytes), source, applicationRoot, explicitlyApproved: true));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Stage_HonorsPreCanceledOperationBeforeCreatingApplicationStorage()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WhisperRuntimeStager().StageAsync(
                RuntimeCandidate(bytes),
                source,
                applicationRoot,
                explicitlyApproved: true,
                cancellation.Token));

        Assert.False(Directory.Exists(applicationRoot));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static PinnedLocalModelDependencyCandidate RuntimeCandidate(byte[] bytes) => new(
        "test-runtime",
        LocalModelDependencyKind.WhisperRuntimeArchive,
        "Test Whisper runtime",
        new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/test/runtime.zip"),
        bytes.Length,
        Convert.ToHexStringLower(SHA256.HashData(bytes)),
        new LocalModelDependencyLicense(
            "MIT plus contained notices",
            new Uri("https://raw.githubusercontent.com/ggml-org/whisper.cpp/test/LICENSE"),
            100,
            new string('a', 64),
            true),
        []);

    private static byte[] RuntimeArchiveBytes(params RuntimeEntry[] entries)
    {
        if (entries.Length == 0)
        {
            entries =
            [
                new RuntimeEntry("Release/", []),
                new RuntimeEntry("Release/whisper-cli.exe", [1, 2, 3, 4]),
                new RuntimeEntry("Release/cublas64_11.dll", [5, 6, 7, 8]),
                new RuntimeEntry("LICENSE", Encoding.UTF8.GetBytes("MIT fixture license")),
            ];
        }
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var specification in entries)
            {
                var entry = archive.CreateEntry(specification.Name, CompressionLevel.Optimal);
                if (specification.Content.Length == 0) continue;
                using var content = entry.Open();
                content.Write(specification.Content);
            }
        }
        return bytes.ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record RuntimeEntry(string Name, byte[] Content);
}
