using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DCx.Core.LocalModels;
using DCx.Core.Security;

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
        var anchorStore = new MemoryStageAnchorStore();

        var result = await new WhisperRuntimeStager(
            new FixedTimeProvider(StagedUtc),
            anchorStore).StageAsync(
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
        var anchor = await anchorStore.ReadAsync(candidate);
        Assert.NotNull(anchor);
        Assert.Equal(StagedUtc, anchor.StagedUtc);
        Assert.Equal(new FileInfo(result.ReceiptPath).Length, anchor.ReceiptBytes);
        Assert.Equal(64, anchor.ReceiptSha256.Length);
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

    [Fact]
    public async Task Stage_FailsClosedWhenProtectedAnchorCannotBeWrittenAndPublishesNothing()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(bytes);
        var applicationRoot = Path.Combine(root, "application");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager(
                new FixedTimeProvider(StagedUtc),
                new RejectingStageAnchorStore()).StageAsync(
                    candidate,
                    source,
                    applicationRoot,
                    explicitlyApproved: true));

        var runtimeRoot = Path.Combine(
            applicationRoot,
            "local-models",
            "whisper-runtime");
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtimeRoot));
        Assert.False(File.Exists(Path.Combine(applicationRoot, "local-models", "whisper.json")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Stage_FailsBeforeCopyOrExtractionWhenProtectedAnchorCannotBeRead()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var applicationRoot = Path.Combine(root, "application");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStager(
                new FixedTimeProvider(StagedUtc),
                new ReadRejectingStageAnchorStore()).StageAsync(
                    RuntimeCandidate(bytes),
                    source,
                    applicationRoot,
                    explicitlyApproved: true));

        var runtimeRoot = Path.Combine(applicationRoot, "local-models", "whisper-runtime");
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtimeRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Stage_ReusesProtectedTimestampWhenExactRemovedStageIsRecreated()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(bytes);
        var applicationRoot = Path.Combine(root, "application");
        var anchorStore = new MemoryStageAnchorStore();
        var first = await new WhisperRuntimeStager(
            new FixedTimeProvider(StagedUtc),
            anchorStore).StageAsync(
                candidate,
                source,
                applicationRoot,
                explicitlyApproved: true);
        var firstReceipt = await File.ReadAllBytesAsync(first.ReceiptPath);
        Directory.Delete(first.RuntimeDirectory, recursive: true);

        var second = await new WhisperRuntimeStager(
            new FixedTimeProvider(StagedUtc.AddDays(1)),
            anchorStore).StageAsync(
                candidate,
                source,
                applicationRoot,
                explicitlyApproved: true);

        Assert.Equal(StagedUtc, second.StagedUtc);
        Assert.Equal(firstReceipt, await File.ReadAllBytesAsync(second.ReceiptPath));
        Assert.Equal(StagedUtc, (await anchorStore.ReadAsync(candidate))?.StagedUtc);
    }

    [Fact]
    public async Task Stage_DefaultWindowsVaultAnchorRoundTripsThroughDefaultVerifierAndIsCleanedUp()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(
            bytes,
            "test-runtime-" + Guid.NewGuid().ToString("N"));
        var applicationRoot = Path.Combine(root, "application");
        var target = WindowsWhisperRuntimeStageAnchorStore.TargetName(
            candidate.Id,
            candidate.Sha256);
        var vault = new WindowsCredentialVault();
        try
        {
            var staged = await new WhisperRuntimeStager(
                new FixedTimeProvider(StagedUtc)).StageAsync(
                    candidate,
                    source,
                    applicationRoot,
                    explicitlyApproved: true);
            var verified = await new WhisperRuntimeStageVerifier().VerifyAsync(
                candidate,
                applicationRoot);

            Assert.Equal(staged.ReceiptPath, verified.ReceiptPath);
            Assert.NotNull(vault.Read(target));
            Assert.False(File.Exists(Path.Combine(applicationRoot, "local-models", "whisper.json")));
        }
        finally
        {
            vault.Delete(target);
        }
    }

    [Fact]
    public async Task Verify_RehashesExactStageWithoutRegisteringOrExecutingRuntime()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();

        var verified = await new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(
            candidate,
            applicationRoot);

        Assert.Equal(staged.CandidateId, verified.CandidateId);
        Assert.Equal(staged.ArchiveSha256, verified.ArchiveSha256);
        Assert.Equal(staged.RuntimeDirectory, verified.RuntimeDirectory);
        Assert.Equal(staged.WhisperCliPath, verified.WhisperCliPath);
        Assert.Equal(staged.ReceiptPath, verified.ReceiptPath);
        Assert.Equal(staged.StagedUtc, verified.StagedUtc);
        Assert.Equal(staged.FileCount, verified.FileCount);
        Assert.Equal(staged.DirectoryCount, verified.DirectoryCount);
        Assert.Equal(staged.ExpandedBytes, verified.ExpandedBytes);
        Assert.Equal(staged.LicenseNoticeEntries, verified.LicenseNoticeEntries);
        Assert.Equal(staged.Files, verified.Files);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(verified.WhisperCliPath));
        Assert.False(File.Exists(Path.Combine(applicationRoot, "local-models", "whisper.json")));
    }

    [Fact]
    public async Task Verify_DeniesTamperedRuntimeFile()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();
        await File.WriteAllBytesAsync(staged.WhisperCliPath, [4, 3, 2, 1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));
    }

    [Fact]
    public async Task Verify_DeniesUnexpectedFileAndDirectory()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();
        var unexpectedFile = Path.Combine(staged.RuntimeDirectory, "unexpected.dll");
        await File.WriteAllBytesAsync(unexpectedFile, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        File.Delete(unexpectedFile);
        Directory.CreateDirectory(Path.Combine(staged.RuntimeDirectory, "unexpected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));
    }

    [Fact]
    public async Task Verify_DeniesLinkedContentWithoutReadingOutside()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve me");
        Directory.CreateSymbolicLink(
            Path.Combine(staged.RuntimeDirectory, "Release", "unsafe"),
            outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        Assert.Equal("preserve me", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Verify_DeniesUnknownDuplicateOrMismatchedReceiptIdentity()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();
        var receipt = await File.ReadAllTextAsync(staged.ReceiptPath);
        await File.WriteAllTextAsync(staged.ReceiptPath, receipt.Replace(
            "{",
            "{\"unexpected\":true,",
            StringComparison.Ordinal));
        await anchorStore.ForceReceiptAsync(candidate, staged.ReceiptPath, StagedUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        await File.WriteAllTextAsync(staged.ReceiptPath, receipt.Replace(
            "{",
            $"{{\"schemaVersion\":{WhisperRuntimeStager.CurrentReceiptSchemaVersion},",
            StringComparison.Ordinal));
        await anchorStore.ForceReceiptAsync(candidate, staged.ReceiptPath, StagedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        await File.WriteAllTextAsync(staged.ReceiptPath, receipt.Replace(
            "\"candidateId\": \"test-runtime\"",
            "\"candidateId\": \"different-runtime\"",
            StringComparison.Ordinal));
        await anchorStore.ForceReceiptAsync(candidate, staged.ReceiptPath, StagedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        var receiptNode = JsonNode.Parse(receipt)?.AsObject()
            ?? throw new InvalidOperationException("The generated fixture receipt was unavailable.");
        receiptNode["licenseNoticeEntries"] = new JsonArray();
        await File.WriteAllTextAsync(staged.ReceiptPath, receiptNode.ToJsonString());
        await anchorStore.ForceReceiptAsync(candidate, staged.ReceiptPath, StagedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));
    }

    [Fact]
    public async Task Verify_DeniesMissingAnchorAndCoordinatedReceiptPayloadReplacement()
    {
        var (candidate, applicationRoot, staged, anchorStore) = await StageFixtureAsync();
        var protectedAnchor = Assert.IsType<WhisperRuntimeStageAnchor>(
            await anchorStore.ReadAsync(candidate));
        anchorStore.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));

        anchorStore.Set(protectedAnchor);
        var changedCli = new byte[] { 4, 3, 2, 1 };
        await File.WriteAllBytesAsync(staged.WhisperCliPath, changedCli);
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(staged.ReceiptPath))?.AsObject()
            ?? throw new InvalidOperationException("The generated fixture receipt was unavailable.");
        var files = receipt["files"]?.AsArray()
            ?? throw new InvalidOperationException("The generated fixture file inventory was unavailable.");
        var cli = files.Select(item => item?.AsObject()).Single(item =>
            string.Equals(
                item?["relativePath"]?.GetValue<string>(),
                "Release/whisper-cli.exe",
                StringComparison.Ordinal));
        cli!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(changedCli));
        await File.WriteAllTextAsync(staged.ReceiptPath, receipt.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(candidate, applicationRoot));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static PinnedLocalModelDependencyCandidate RuntimeCandidate(
        byte[] bytes,
        string id = "test-runtime") => new(
        id,
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

    private async Task<(
        PinnedLocalModelDependencyCandidate Candidate,
        string ApplicationRoot,
        WhisperRuntimeStagingResult Staged,
        MemoryStageAnchorStore AnchorStore)> StageFixtureAsync()
    {
        Directory.CreateDirectory(root);
        var bytes = RuntimeArchiveBytes();
        var source = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(source, bytes);
        var candidate = RuntimeCandidate(bytes);
        var applicationRoot = Path.Combine(root, "application");
        var anchorStore = new MemoryStageAnchorStore();
        var staged = await new WhisperRuntimeStager(
            new FixedTimeProvider(StagedUtc),
            anchorStore).StageAsync(
                candidate,
                source,
                applicationRoot,
                explicitlyApproved: true);
        return (candidate, applicationRoot, staged, anchorStore);
    }

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

    private sealed class MemoryStageAnchorStore : IWhisperRuntimeStageAnchorStore
    {
        private WhisperRuntimeStageAnchor? anchor;

        public Task<WhisperRuntimeStageAnchor?> ReadAsync(
            PinnedLocalModelDependencyCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(anchor is not null &&
                string.Equals(anchor.CandidateId, candidate.Id, StringComparison.Ordinal) &&
                string.Equals(anchor.ArchiveSha256, candidate.Sha256, StringComparison.Ordinal)
                    ? anchor
                    : null);
        }

        public Task BindAsync(
            WhisperRuntimeStageAnchor value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchor is not null && anchor != value)
            {
                throw new InvalidOperationException("The protected fixture anchor conflicts.");
            }
            anchor = value;
            return Task.CompletedTask;
        }

        public async Task ForceReceiptAsync(
            PinnedLocalModelDependencyCandidate candidate,
            string receiptPath,
            DateTimeOffset stagedUtc)
        {
            var bytes = await File.ReadAllBytesAsync(receiptPath);
            try
            {
                anchor = new WhisperRuntimeStageAnchor(
                    WindowsWhisperRuntimeStageAnchorStore.CurrentSchemaVersion,
                    candidate.Id,
                    candidate.Sha256,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    bytes.Length,
                    stagedUtc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Clear() => anchor = null;

        public void Set(WhisperRuntimeStageAnchor value) => anchor = value;
    }

    private sealed class RejectingStageAnchorStore : IWhisperRuntimeStageAnchorStore
    {
        public Task<WhisperRuntimeStageAnchor?> ReadAsync(
            PinnedLocalModelDependencyCandidate candidate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WhisperRuntimeStageAnchor?>(null);

        public Task BindAsync(
            WhisperRuntimeStageAnchor anchor,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("The protected anchor is unavailable."));
    }

    private sealed class ReadRejectingStageAnchorStore : IWhisperRuntimeStageAnchorStore
    {
        public Task<WhisperRuntimeStageAnchor?> ReadAsync(
            PinnedLocalModelDependencyCandidate candidate,
            CancellationToken cancellationToken = default) =>
            Task.FromException<WhisperRuntimeStageAnchor?>(
                new InvalidOperationException("The protected anchor cannot be read."));

        public Task BindAsync(
            WhisperRuntimeStageAnchor anchor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Binding must not be reached.");
    }

    private sealed record RuntimeEntry(string Name, byte[] Content);
}
