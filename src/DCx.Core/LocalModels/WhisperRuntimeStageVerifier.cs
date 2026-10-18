using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

/// <summary>
/// Revalidates an inert, digest-bound Whisper runtime stage against its separately protected
/// receipt anchor without registering or executing it. The result is a point-in-time integrity
/// observation, not publisher authenticity or execution approval.
/// </summary>
public sealed class WhisperRuntimeStageVerifier
{
    private const int MaximumFiles = 1024;
    private const int MaximumDirectories = 1024;
    private const long MaximumExpandedBytes = 3L * 1024 * 1024 * 1024;
    private const int MaximumReportedLicenseEntries = 32;

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly IWhisperRuntimeStageAnchorStore anchorStore;

    public WhisperRuntimeStageVerifier()
        : this(new WindowsWhisperRuntimeStageAnchorStore())
    {
    }

    public WhisperRuntimeStageVerifier(IWhisperRuntimeStageAnchorStore anchorStore) =>
        this.anchorStore = anchorStore ?? throw new ArgumentNullException(nameof(anchorStore));

    public async Task<WhisperRuntimeStagingResult> VerifyAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
        if (candidate.Kind != LocalModelDependencyKind.WhisperRuntimeArchive)
        {
            throw new InvalidOperationException("Only a pinned Whisper runtime stage can be verified here.");
        }

        var applicationRoot = LocalModelStoragePathGuard.RequireDirectory(
            applicationDataRoot,
            "Whisper runtime application-data");
        var localModelsRoot = LocalModelStoragePathGuard.RequireDirectory(
            Path.Combine(applicationRoot, "local-models"),
            "local-model storage");
        var runtimeRoot = LocalModelStoragePathGuard.RequireDirectory(
            Path.Combine(localModelsRoot, "whisper-runtime"),
            "Whisper runtime storage");
        var destinationName = $"{candidate.Id}-{candidate.Sha256}";
        var runtimeDirectory = LocalModelStoragePathGuard.RequireDirectory(
            Path.Combine(runtimeRoot, destinationName),
            "staged Whisper runtime");
        if (!string.Equals(
                Path.GetFileName(runtimeDirectory),
                destinationName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The staged Whisper runtime destination identity is invalid.");
        }

        var receiptPath = LocalModelStoragePathGuard.ValidateFile(
            Path.Combine(runtimeDirectory, WhisperRuntimeStager.ReceiptFileName),
            mustExist: true,
            "Whisper runtime receipt");
        var receiptInfo = new FileInfo(receiptPath);
        if (receiptInfo.Length is <= 0 or > WhisperRuntimeStager.MaximumReceiptBytes)
        {
            throw new InvalidOperationException("The Whisper runtime receipt is outside its size limit.");
        }

        await using var receiptStream = new FileStream(
            receiptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        LocalModelStoragePathGuard.ValidateFile(receiptPath, mustExist: true, "Whisper runtime receipt");
        if (receiptStream.Length != receiptInfo.Length)
        {
            throw new InvalidOperationException("The Whisper runtime receipt changed before verification.");
        }

        var receiptBytes = new byte[checked((int)receiptStream.Length)];
        RuntimeReceiptDto receipt;
        WhisperRuntimeStageAnchor anchor;
        try
        {
            await receiptStream.ReadExactlyAsync(receiptBytes, cancellationToken).ConfigureAwait(false);
            var receiptDigestBytes = SHA256.HashData(receiptBytes);
            string receiptSha256;
            try
            {
                receiptSha256 = Convert.ToHexStringLower(receiptDigestBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(receiptDigestBytes);
            }
            anchor = await anchorStore.ReadAsync(candidate, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The staged Whisper runtime has no protected receipt anchor.");
            if (anchor.ReceiptBytes != receiptStream.Length)
            {
                throw new InvalidOperationException(
                    "The staged Whisper runtime receipt length does not match its protected anchor.");
            }
            LocalModelDependencyCatalogLoader.RequireDigest(
                receiptSha256,
                anchor.ReceiptSha256,
                "protected Whisper runtime stage receipt");
            using var document = JsonDocument.Parse(receiptBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(document.RootElement);
            receipt = JsonSerializer.Deserialize<RuntimeReceiptDto>(receiptBytes, ReceiptJsonOptions)
                ?? throw new InvalidOperationException("The Whisper runtime receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Whisper runtime receipt is malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receiptBytes);
        }

        var files = ValidateReceipt(receipt, candidate);
        if (receipt.StagedUtc != anchor.StagedUtc)
        {
            throw new InvalidOperationException(
                "The staged Whisper runtime timestamp does not match its protected anchor.");
        }
        var expectedDirectories = BuildExpectedDirectories(files);
        var expectedFiles = files.ToDictionary(
            file => file.RelativePath,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var observedFiles = new HashSet<string>(expectedFiles.Comparer);
        var observedDirectories = new HashSet<string>(expectedFiles.Comparer);
        var leases = new List<FileStream>(files.Count);
        try
        {
            var pending = new Stack<string>();
            pending.Push(runtimeDirectory);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = LocalModelStoragePathGuard.RequireDirectory(
                    pending.Pop(),
                    "staged Whisper runtime directory");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("The staged Whisper runtime contains an unsafe link.");
                    }

                    var relative = Path.GetRelativePath(runtimeDirectory, entry)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        relative = WhisperRuntimeArchiveInspector.ValidateStagedFileRelativePath(relative);
                        if (!expectedDirectories.Contains(relative) || !observedDirectories.Add(relative))
                        {
                            throw new InvalidOperationException(
                                "The staged Whisper runtime contains an unexpected directory.");
                        }
                        pending.Push(entry);
                        continue;
                    }

                    var canonicalFile = LocalModelStoragePathGuard.ValidateFile(
                        entry,
                        mustExist: true,
                        "staged Whisper runtime file");
                    if (string.Equals(
                            relative,
                            WhisperRuntimeStager.ReceiptFileName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(canonicalFile, receiptPath,
                                OperatingSystem.IsWindows()
                                    ? StringComparison.OrdinalIgnoreCase
                                    : StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("The Whisper runtime receipt identity changed.");
                        }
                        continue;
                    }

                    relative = WhisperRuntimeArchiveInspector.ValidateStagedFileRelativePath(relative);
                    if (!expectedFiles.TryGetValue(relative, out var expected) || !observedFiles.Add(relative))
                    {
                        throw new InvalidOperationException("The staged Whisper runtime contains an unexpected file.");
                    }

                    var lease = new FileStream(
                        canonicalFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    leases.Add(lease);
                    LocalModelStoragePathGuard.ValidateFile(
                        canonicalFile,
                        mustExist: true,
                        "staged Whisper runtime file");
                    if (lease.Length != expected.Bytes)
                    {
                        throw new InvalidOperationException("A staged Whisper runtime file length changed.");
                    }
                    var digest = Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
                    LocalModelDependencyCatalogLoader.RequireDigest(
                        digest,
                        expected.Sha256,
                        "staged Whisper runtime file");
                }
            }

            if (observedFiles.Count != expectedFiles.Count ||
                observedDirectories.Count != expectedDirectories.Count)
            {
                throw new InvalidOperationException("The staged Whisper runtime inventory is incomplete.");
            }
            LocalModelStoragePathGuard.ValidateFile(receiptPath, mustExist: true, "Whisper runtime receipt");
            if (receiptStream.Length != receiptInfo.Length)
            {
                throw new InvalidOperationException("The Whisper runtime receipt changed during verification.");
            }

            var cliPath = LocalModelStoragePathGuard.ValidateFile(
                CombineUnder(runtimeDirectory, receipt.WhisperCliRelativePath!),
                mustExist: true,
                "staged Whisper CLI");
            return new WhisperRuntimeStagingResult(
                receipt.CandidateId!,
                receipt.ArchiveSha256!,
                runtimeDirectory,
                cliPath,
                receiptPath,
                receipt.StagedUtc,
                receipt.FileCount,
                receipt.DirectoryCount,
                receipt.ExpandedBytes,
                receipt.LicenseNoticeEntryCount,
                Array.AsReadOnly(receipt.LicenseNoticeEntries!),
                files);
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<WhisperRuntimeStagedFile> ValidateReceipt(
        RuntimeReceiptDto receipt,
        PinnedLocalModelDependencyCandidate candidate)
    {
        if (receipt.SchemaVersion != WhisperRuntimeStager.CurrentReceiptSchemaVersion ||
            !string.Equals(receipt.CandidateId, candidate.Id, StringComparison.Ordinal) ||
            !string.Equals(receipt.ArchiveSha256, candidate.Sha256, StringComparison.Ordinal) ||
            receipt.ArchiveBytes != candidate.ExpectedBytes ||
            !string.Equals(receipt.CatalogLicenseIdentifier, candidate.License.Identifier, StringComparison.Ordinal) ||
            !string.Equals(receipt.CatalogLicenseSha256, candidate.License.Sha256, StringComparison.Ordinal) ||
            receipt.StagedUtc == default || receipt.StagedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The Whisper runtime receipt does not match the pinned candidate.");
        }
        if (receipt.Files is null || receipt.FileCount is < 1 or > MaximumFiles ||
            receipt.Files.Length != receipt.FileCount ||
            receipt.DirectoryCount is < 0 or > MaximumDirectories ||
            receipt.ExpandedBytes is < 1 or > MaximumExpandedBytes ||
            receipt.LicenseNoticeEntryCount < 0 ||
            receipt.LicenseNoticeEntryCount > receipt.FileCount ||
            receipt.LicenseNoticeEntries is null ||
            receipt.LicenseNoticeEntries.Length != Math.Min(
                receipt.LicenseNoticeEntryCount,
                MaximumReportedLicenseEntries))
        {
            throw new InvalidOperationException("The Whisper runtime receipt inventory is invalid.");
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        var files = new List<WhisperRuntimeStagedFile>(receipt.Files.Length);
        long expandedBytes = 0;
        foreach (var file in receipt.Files)
        {
            if (file is null || file.Bytes < 0 || file.Bytes > MaximumExpandedBytes)
            {
                throw new InvalidOperationException("The Whisper runtime receipt contains an invalid file.");
            }
            var relative = WhisperRuntimeArchiveInspector.ValidateStagedFileRelativePath(file.RelativePath);
            if (string.Equals(relative, WhisperRuntimeStager.ReceiptFileName, StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(relative))
            {
                throw new InvalidOperationException("The Whisper runtime receipt contains a duplicate file.");
            }
            LocalModelDependencyCatalogLoader.ValidateSha256(file.Sha256, "staged runtime file");
            expandedBytes = checked(expandedBytes + file.Bytes);
            files.Add(new WhisperRuntimeStagedFile(relative, file.Bytes, file.Sha256!));
        }
        if (expandedBytes != receipt.ExpandedBytes)
        {
            throw new InvalidOperationException("The Whisper runtime receipt byte total is invalid.");
        }

        var cli = WhisperRuntimeArchiveInspector.ValidateStagedFileRelativePath(
            receipt.WhisperCliRelativePath);
        if (!paths.Contains(cli) ||
            !string.Equals(Path.GetFileName(cli), "whisper-cli.exe", StringComparison.OrdinalIgnoreCase) ||
            files.Count(file => comparer.Equals(file.RelativePath, cli)) != 1)
        {
            throw new InvalidOperationException("The Whisper runtime receipt CLI identity is invalid.");
        }
        var notices = new HashSet<string>(comparer);
        foreach (var notice in receipt.LicenseNoticeEntries)
        {
            var relative = WhisperRuntimeArchiveInspector.ValidateStagedFileRelativePath(notice);
            if (!paths.Contains(relative) || !notices.Add(relative))
            {
                throw new InvalidOperationException("The Whisper runtime receipt license inventory is invalid.");
            }
        }

        return Array.AsReadOnly(files.OrderBy(file => file.RelativePath, comparer).ToArray());
    }

    private static HashSet<string> BuildExpectedDirectories(
        IReadOnlyList<WhisperRuntimeStagedFile> files)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new HashSet<string>(comparer);
        foreach (var file in files)
        {
            var slash = file.RelativePath.LastIndexOf('/');
            while (slash > 0)
            {
                result.Add(file.RelativePath[..slash]);
                slash = file.RelativePath.LastIndexOf('/', slash - 1);
            }
        }
        if (result.Count > MaximumDirectories)
        {
            throw new InvalidOperationException("The Whisper runtime receipt has too many directories.");
        }
        return result;
    }

    private static string CombineUnder(string root, string relativePath)
    {
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

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("The Whisper runtime receipt has duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private sealed record RuntimeReceiptDto(
        int SchemaVersion,
        string? CandidateId,
        string? ArchiveSha256,
        long ArchiveBytes,
        string? CatalogLicenseIdentifier,
        string? CatalogLicenseSha256,
        DateTimeOffset StagedUtc,
        string? WhisperCliRelativePath,
        int FileCount,
        int DirectoryCount,
        long ExpandedBytes,
        int LicenseNoticeEntryCount,
        string[]? LicenseNoticeEntries,
        RuntimeFileDto?[]? Files);

    private sealed record RuntimeFileDto(
        string? RelativePath,
        long Bytes,
        string? Sha256);
}
