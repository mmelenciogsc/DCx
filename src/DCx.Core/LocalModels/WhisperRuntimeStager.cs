using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DCx.Core.LocalModels;

public sealed record WhisperRuntimeStagedFile(
    string RelativePath,
    long Bytes,
    string Sha256);

public sealed record WhisperRuntimeStagingResult(
    string CandidateId,
    string ArchiveSha256,
    string RuntimeDirectory,
    string WhisperCliPath,
    string ReceiptPath,
    DateTimeOffset StagedUtc,
    int FileCount,
    int DirectoryCount,
    long ExpandedBytes,
    int LicenseNoticeEntryCount,
    IReadOnlyList<string> LicenseNoticeEntries,
    IReadOnlyList<WhisperRuntimeStagedFile> Files);

public sealed class WhisperRuntimeStager
{
    public const int CurrentReceiptSchemaVersion = 1;
    public const string ReceiptFileName = ".dcx-runtime-receipt.json";
    internal const int MaximumReceiptBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 8,
    };

    private readonly TimeProvider timeProvider;
    private readonly LocalModelDependencyArtifactReviewer reviewer;
    private readonly IWhisperRuntimeStageAnchorStore anchorStore;

    public WhisperRuntimeStager(
        TimeProvider? timeProvider = null,
        IWhisperRuntimeStageAnchorStore? anchorStore = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.anchorStore = anchorStore ?? new WindowsWhisperRuntimeStageAnchorStore();
        reviewer = new LocalModelDependencyArtifactReviewer(this.timeProvider);
    }

    public async Task<WhisperRuntimeStagingResult> StageAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string archivePath,
        string applicationDataRoot,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException("Staging a Whisper runtime requires explicit user approval.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
        if (candidate.Kind != LocalModelDependencyKind.WhisperRuntimeArchive)
        {
            throw new InvalidOperationException("Only a pinned Whisper runtime archive can be staged here.");
        }

        var source = LocalModelStoragePathGuard.ValidateFile(
            archivePath,
            mustExist: true,
            "Whisper runtime source archive");
        if (!string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Whisper runtime source must be a ZIP archive.");
        }
        if (new FileInfo(source).Length != candidate.ExpectedBytes)
        {
            throw new InvalidOperationException("The Whisper runtime source length does not match the pinned value.");
        }

        var applicationRoot = LocalModelStoragePathGuard.EnsureDirectory(
            applicationDataRoot,
            "Whisper runtime application-data");
        var localModelsRoot = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(applicationRoot, "local-models"),
            "local-model storage");
        var runtimeRoot = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(localModelsRoot, "whisper-runtime"),
            "Whisper runtime storage");
        var destinationName = $"{candidate.Id}-{candidate.Sha256}";
        EnsureDestinationAbsent(runtimeRoot, destinationName);
        var existingAnchor = await anchorStore.ReadAsync(candidate, cancellationToken)
            .ConfigureAwait(false);
        var destination = Path.Combine(runtimeRoot, destinationName);
        var operationId = Guid.NewGuid().ToString("N");
        var temporaryDirectory = Path.Combine(runtimeRoot, $".stage-{operationId}");
        var privateArchive = Path.Combine(runtimeRoot, $".source-{operationId}.zip");

        try
        {
            LocalModelStoragePathGuard.RequireDirectory(runtimeRoot, "Whisper runtime storage");
            _ = LocalModelStoragePathGuard.EnsureDirectory(
                temporaryDirectory,
                "temporary Whisper runtime");
            await CopySourceAsync(source, privateArchive, candidate.ExpectedBytes, cancellationToken)
                .ConfigureAwait(false);

            var extraction = await ReviewAndExtractAsync(
                    candidate,
                    privateArchive,
                    temporaryDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            var stagedUtc = existingAnchor?.StagedUtc ?? timeProvider.GetUtcNow();
            if (stagedUtc == default || stagedUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("The Whisper runtime staging clock did not return UTC.");
            }

            var receiptPath = Path.Combine(temporaryDirectory, ReceiptFileName);
            await WriteReceiptAsync(
                    receiptPath,
                    candidate,
                    extraction.Inventory,
                    extraction.Files,
                    stagedUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            LocalModelStoragePathGuard.RequireDirectory(runtimeRoot, "Whisper runtime storage");
            LocalModelStoragePathGuard.RequireDirectory(temporaryDirectory, "temporary Whisper runtime");
            foreach (var file in extraction.Files)
            {
                LocalModelStoragePathGuard.ValidateFile(
                    CombineUnder(temporaryDirectory, file.RelativePath),
                    mustExist: true,
                    "staged Whisper runtime file");
            }
            LocalModelStoragePathGuard.ValidateFile(
                receiptPath,
                mustExist: true,
                "Whisper runtime receipt");
            LocalModelStoragePathGuard.TryDeleteTemporary(
                runtimeRoot,
                privateArchive,
                "private Whisper runtime source");
            if (File.Exists(privateArchive))
            {
                throw new InvalidOperationException("The private Whisper runtime source could not be removed safely.");
            }

            await BindReceiptAsync(
                    candidate,
                    receiptPath,
                    stagedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureDestinationAbsent(runtimeRoot, destinationName);
            Directory.Move(temporaryDirectory, destination);
            _ = LocalModelStoragePathGuard.RequireDirectory(
                destination,
                "staged Whisper runtime");
            return await new WhisperRuntimeStageVerifier(anchorStore).VerifyAsync(
                    candidate,
                    applicationRoot,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            LocalModelStoragePathGuard.TryDeleteTemporary(
                runtimeRoot,
                privateArchive,
                "private Whisper runtime source");
            LocalModelStoragePathGuard.TryDeleteTemporaryDirectory(
                runtimeRoot,
                temporaryDirectory,
                "temporary Whisper runtime");
        }
    }

    private async Task BindReceiptAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string receiptPath,
        DateTimeOffset stagedUtc,
        CancellationToken cancellationToken)
    {
        var canonical = LocalModelStoragePathGuard.ValidateFile(
            receiptPath,
            mustExist: true,
            "Whisper runtime receipt anchor source");
        await using var receipt = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        LocalModelStoragePathGuard.ValidateFile(
            canonical,
            mustExist: true,
            "Whisper runtime receipt anchor source");
        if (receipt.Length is <= 0 or > MaximumReceiptBytes)
        {
            throw new InvalidOperationException("The Whisper runtime receipt anchor source is outside its size limit.");
        }
        var digestBytes = await SHA256.HashDataAsync(receipt, cancellationToken).ConfigureAwait(false);
        string digest;
        try
        {
            digest = Convert.ToHexStringLower(digestBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digestBytes);
        }
        LocalModelStoragePathGuard.ValidateFile(
            canonical,
            mustExist: true,
            "Whisper runtime receipt anchor source");
        await anchorStore.BindAsync(
                new WhisperRuntimeStageAnchor(
                    WindowsWhisperRuntimeStageAnchorStore.CurrentSchemaVersion,
                    candidate.Id,
                    candidate.Sha256,
                    digest,
                    receipt.Length,
                    stagedUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExtractionReview> ReviewAndExtractAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string privateArchive,
        string temporaryDirectory,
        CancellationToken cancellationToken)
    {
        var canonicalArchive = LocalModelStoragePathGuard.ValidateFile(
            privateArchive,
            mustExist: true,
            "private Whisper runtime source");
        await using var archiveStream = new FileStream(
            canonicalArchive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var review = await reviewer.ReviewLeasedAsync(
                candidate,
                canonicalArchive,
                archiveStream,
                cancellationToken)
            .ConfigureAwait(false);
        var inventory = review.RuntimeArchive
            ?? throw new InvalidOperationException("The reviewed artifact has no Whisper runtime inventory.");

        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<WhisperRuntimeStagedFile>(inventory.FileCount);
        var fileCount = 0;
        var directoryCount = 0;
        long expandedBytes = 0;
        var buffer = new byte[1024 * 1024];
        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryReview = WhisperRuntimeArchiveInspector.ValidateEntry(entry, names);
                if (string.Equals(
                        entryReview.CanonicalPath,
                        ReceiptFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The Whisper runtime archive collides with its staging receipt.");
                }
                if (entryReview.IsDirectory)
                {
                    // Empty archive directories carry no runtime content. Parent directories are
                    // created from the exact file inventory so a later verifier can deny every
                    // unexpected filesystem entry.
                    directoryCount++;
                    continue;
                }

                var target = CombineUnder(temporaryDirectory, entryReview.CanonicalPath);
                var parent = Path.GetDirectoryName(target)
                    ?? throw new InvalidOperationException("A staged Whisper runtime file has no parent directory.");
                _ = LocalModelStoragePathGuard.EnsureDirectory(parent, "staged Whisper runtime directory");
                LocalModelStoragePathGuard.ValidateFile(target, mustExist: false, "staged Whisper runtime file");
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
                await using var content = entry.Open();
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long observed = 0;
                var crc32 = uint.MaxValue;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    observed = checked(observed + read);
                    if (observed > entry.Length)
                    {
                        throw new InvalidOperationException("A Whisper runtime entry exceeded its reviewed size during staging.");
                    }
                    sha256.AppendData(buffer, 0, read);
                    crc32 = WhisperRuntimeArchiveInspector.UpdateCrc32(crc32, buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (observed != entry.Length || ~crc32 != entry.Crc32)
                {
                    throw new InvalidOperationException("A Whisper runtime entry changed during staging.");
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var hash = sha256.GetHashAndReset();
                try
                {
                    files.Add(new WhisperRuntimeStagedFile(
                        entryReview.CanonicalPath,
                        observed,
                        Convert.ToHexStringLower(hash)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
                fileCount++;
                expandedBytes = checked(expandedBytes + observed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (fileCount != inventory.FileCount || directoryCount != inventory.DirectoryCount ||
            expandedBytes != inventory.ExpandedBytes ||
            !files.Any(file => string.Equals(
                file.RelativePath,
                inventory.WhisperCliEntry,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The staged Whisper runtime inventory changed after review.");
        }
        var orderedFiles = files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ExtractionReview(inventory, Array.AsReadOnly(orderedFiles));
    }

    private static async Task CopySourceAsync(
        string source,
        string privateArchive,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        LocalModelStoragePathGuard.ValidateFile(source, mustExist: true, "Whisper runtime source archive");
        if (input.Length != expectedBytes)
        {
            throw new InvalidOperationException("The Whisper runtime source length changed before staging.");
        }
        LocalModelStoragePathGuard.ValidateFile(
            privateArchive,
            mustExist: false,
            "private Whisper runtime source");
        await using var output = new FileStream(
            privateArchive,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > expectedBytes)
                {
                    throw new InvalidOperationException("The Whisper runtime source exceeded its pinned size.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (copied != expectedBytes)
            {
                throw new InvalidOperationException("The Whisper runtime source length changed during staging.");
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task WriteReceiptAsync(
        string path,
        PinnedLocalModelDependencyCandidate candidate,
        WhisperRuntimeArchiveInventory inventory,
        IReadOnlyList<WhisperRuntimeStagedFile> files,
        DateTimeOffset stagedUtc,
        CancellationToken cancellationToken)
    {
        var receipt = new RuntimeReceipt(
            CurrentReceiptSchemaVersion,
            candidate.Id,
            candidate.Sha256,
            candidate.ExpectedBytes,
            candidate.License.Identifier,
            candidate.License.Sha256,
            stagedUtc,
            inventory.WhisperCliEntry,
            inventory.FileCount,
            inventory.DirectoryCount,
            inventory.ExpandedBytes,
            inventory.LicenseNoticeEntryCount,
            inventory.LicenseNoticeEntries,
            files);
        var content = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptJsonOptions);
        if (content.Length is <= 0 or > MaximumReceiptBytes)
        {
            throw new InvalidOperationException("The Whisper runtime receipt exceeds its size limit.");
        }

        LocalModelStoragePathGuard.ValidateFile(path, mustExist: false, "Whisper runtime receipt");
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CombineUnder(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException("A Whisper runtime relative path is invalid.");
        }
        var combined = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, combined);
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A Whisper runtime path escaped its staging root.");
        }
        return combined;
    }

    private static void EnsureDestinationAbsent(string runtimeRoot, string destinationName)
    {
        LocalModelStoragePathGuard.RequireDirectory(runtimeRoot, "Whisper runtime storage");
        if (Directory.EnumerateFileSystemEntries(runtimeRoot).Any(path => string.Equals(
                Path.GetFileName(path),
                destinationName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The digest-bound Whisper runtime destination already exists.");
        }
    }

    private sealed record ExtractionReview(
        WhisperRuntimeArchiveInventory Inventory,
        IReadOnlyList<WhisperRuntimeStagedFile> Files);

    private sealed record RuntimeReceipt(
        int SchemaVersion,
        string CandidateId,
        string ArchiveSha256,
        long ArchiveBytes,
        string CatalogLicenseIdentifier,
        string CatalogLicenseSha256,
        DateTimeOffset StagedUtc,
        string WhisperCliRelativePath,
        int FileCount,
        int DirectoryCount,
        long ExpandedBytes,
        int LicenseNoticeEntryCount,
        IReadOnlyList<string> LicenseNoticeEntries,
        IReadOnlyList<WhisperRuntimeStagedFile> Files);
}
