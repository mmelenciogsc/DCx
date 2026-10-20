using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Reliability;

public sealed record UserDataBackupEntry(string RelativePath, long Length, string Sha256);

public sealed record UserDataBackupManifest(int Schema, IReadOnlyList<UserDataBackupEntry> Entries);

public sealed record UserDataBackupVerificationResult(int FileCount, long TotalBytes);

public sealed record UserDataRestoreResult(int FileCount, long TotalBytes);

public sealed class UserDataBackupService
{
    public const int MaximumFiles = 10_000;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumManifestBytes = 4L * 1024 * 1024;
    public const string ManifestEntryName = "dcx-backup-manifest.json";

    private static readonly string[] IncludedRoots = ["Browser", "private"];
    private static readonly DateTimeOffset ReproducibleTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public async Task<UserDataBackupManifest> CreateAsync(
        string applicationDataRoot,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateRoot(applicationDataRoot, mustExist: true);
        var target = Path.GetFullPath(destination);
        if (IsWithin(target, sourceRoot))
        {
            throw new InvalidOperationException("The backup destination must be outside the application data root.");
        }
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The backup destination requires a parent directory.");
        Directory.CreateDirectory(targetDirectory);
        RejectReparsePoint(targetDirectory, "The backup destination directory cannot be a reparse point.");
        if (File.Exists(target)) RejectReparsePoint(target, "The existing backup destination cannot be a reparse point.");

        var files = EnumerateIncludedFiles(sourceRoot);
        if (files.Count == 0) throw new InvalidOperationException("There is no approved user data to back up.");
        var temporary = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var entries = new List<UserDataBackupEntry>(files.Count);
        long totalBytes = 0;
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var archiveEntry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                    archiveEntry.LastWriteTime = ReproducibleTimestamp;
                    await using var input = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var entryOutput = archiveEntry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var copied = await CopyBoundedAsync(
                            input,
                            entryOutput,
                            hash,
                            MaximumTotalBytes - totalBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    totalBytes = checked(totalBytes + copied);
                    entries.Add(new UserDataBackupEntry(
                        file.RelativePath,
                        copied,
                        Convert.ToHexStringLower(hash.GetHashAndReset())));
                }

                var manifest = new UserDataBackupManifest(1, entries);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                if (manifestBytes.LongLength > MaximumManifestBytes)
                {
                    throw new InvalidOperationException("The backup manifest exceeds its size limit.");
                }

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                manifestEntry.LastWriteTime = ReproducibleTimestamp;
                await using var manifestOutput = manifestEntry.Open();
                await manifestOutput.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
            return new UserDataBackupManifest(1, entries);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<UserDataBackupVerificationResult> VerifyAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var backup = ValidateBackupPath(backupPath);
        return ReadAndVerifyArchiveAsync(backup, stagingRoot: null, cancellationToken);
    }

    public async Task<UserDataRestoreResult> RestoreAsync(
        string backupPath,
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        var backup = ValidateBackupPath(backupPath);

        var targetRoot = ValidateRoot(applicationDataRoot, mustExist: false);
        if (IsWithin(backup, targetRoot))
        {
            throw new InvalidOperationException("The backup archive must be outside the application data root.");
        }
        var parent = Path.GetDirectoryName(targetRoot)
            ?? throw new InvalidOperationException("The application data root requires a parent directory.");
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent, "The application data parent cannot be a reparse point.");
        if (Directory.Exists(targetRoot)) RejectReparsePoint(targetRoot, "The application data root cannot be a reparse point.");

        var staging = Path.Combine(parent, $".{Path.GetFileName(targetRoot)}.restore-{Guid.NewGuid():N}");
        var rollback = Path.Combine(parent, $".{Path.GetFileName(targetRoot)}.rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var committed = false;
        try
        {
            var verification = await ReadAndVerifyArchiveAsync(backup, staging, cancellationToken)
                .ConfigureAwait(false);
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(rollback);
            ReplaceIncludedRoots(targetRoot, staging, rollback);
            committed = true;
            return new UserDataRestoreResult(verification.FileCount, verification.TotalBytes);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(rollback) &&
                (committed || !Directory.EnumerateFileSystemEntries(rollback).Any()))
            {
                Directory.Delete(rollback, recursive: true);
            }
        }
    }

    private static async Task<UserDataBackupVerificationResult> ReadAndVerifyArchiveAsync(
        string backup,
        string? stagingRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            backup,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 2 or > MaximumFiles + 1)
        {
            throw new InvalidOperationException("The backup archive entry count is invalid.");
        }

        var manifestEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            .ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidOperationException("The backup has no single bounded manifest.");
        }

        var manifestBytes = new byte[checked((int)manifestEntries[0].Length)];
        await using (var manifestStream = manifestEntries[0].Open())
        {
            await manifestStream.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            if (await manifestStream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException("The backup manifest length is invalid.");
            }
        }

        UserDataBackupManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(
                manifestBytes,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            RejectDuplicateJsonProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<UserDataBackupManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidOperationException("The backup manifest is invalid.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The backup manifest is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifestBytes);
        }

        ValidateManifest(manifest);
        var payloadEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal)) continue;
            var relativePath = NormalizeAndValidateRelativePath(entry.FullName);
            if (!payloadEntries.TryAdd(relativePath, entry))
            {
                throw new InvalidOperationException("The backup payload contains a duplicate path.");
            }
        }

        if (payloadEntries.Count != manifest.Entries.Count ||
            payloadEntries.Keys.Except(manifest.Entries.Select(entry => entry.RelativePath), StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("The backup payload does not match its manifest.");
        }

        long totalBytes = 0;
        foreach (var expected in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveEntry = payloadEntries[expected.RelativePath];
            if (archiveEntry.Length != expected.Length)
            {
                throw new InvalidOperationException("A backup entry length does not match its manifest.");
            }

            await using var entryInput = archiveEntry.Open();
            await using var fileOutput = stagingRoot is null
                ? null
                : CreateStagedFile(stagingRoot, expected.RelativePath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var copied = await CopyBoundedAsync(
                    entryInput,
                    fileOutput ?? Stream.Null,
                    hash,
                    MaximumTotalBytes - totalBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            totalBytes = checked(totalBytes + copied);
            if (copied != expected.Length ||
                !string.Equals(
                    Convert.ToHexStringLower(hash.GetHashAndReset()),
                    expected.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A backup entry failed its SHA-256 integrity check.");
            }
        }

        return new UserDataBackupVerificationResult(manifest.Entries.Count, totalBytes);
    }

    private static FileStream CreateStagedFile(string stagingRoot, string relativePath)
    {
        var destination = ResolveContainedPath(stagingRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("The backup manifest contains a duplicate JSON property.");
                }

                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateJsonProperties(item);
        }
    }

    private static string ValidateBackupPath(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var backup = Path.GetFullPath(backupPath);
        if (!File.Exists(backup)) throw new FileNotFoundException("The DCx backup does not exist.", backup);
        RejectReparsePoint(backup, "The backup file cannot be a reparse point.");
        return backup;
    }

    private static void ReplaceIncludedRoots(string targetRoot, string staging, string rollback)
    {
        var backedUp = new List<string>();
        var installed = new List<string>();
        try
        {
            foreach (var root in IncludedRoots)
            {
                var current = Path.Combine(targetRoot, root);
                if (File.Exists(current)) throw new InvalidOperationException($"The existing {root} data is not a directory.");
                if (!Directory.Exists(current)) continue;
                RejectReparsePoint(current, $"The existing {root} data cannot be a reparse point.");
                var previous = Path.Combine(rollback, root);
                Directory.Move(current, previous);
                backedUp.Add(root);
            }

            foreach (var root in IncludedRoots)
            {
                var prepared = Path.Combine(staging, root);
                if (!Directory.Exists(prepared)) continue;
                var current = Path.Combine(targetRoot, root);
                Directory.Move(prepared, current);
                installed.Add(root);
            }
        }
        catch
        {
            foreach (var root in installed.AsEnumerable().Reverse())
            {
                var current = Path.Combine(targetRoot, root);
                if (Directory.Exists(current)) Directory.Delete(current, recursive: true);
            }

            foreach (var root in backedUp.AsEnumerable().Reverse())
            {
                var previous = Path.Combine(rollback, root);
                if (Directory.Exists(previous)) Directory.Move(previous, Path.Combine(targetRoot, root));
            }

            throw;
        }
    }

    private static List<SourceFile> EnumerateIncludedFiles(string sourceRoot)
    {
        var files = new List<SourceFile>();
        foreach (var includedRoot in IncludedRoots)
        {
            var root = Path.Combine(sourceRoot, includedRoot);
            if (!Directory.Exists(root)) continue;
            RejectReparsePoint(root, $"The {includedRoot} backup root cannot be a reparse point.");
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                var current = directories.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    RejectReparsePoint(path, "Backups cannot follow reparse points.");
                    if (Directory.Exists(path))
                    {
                        directories.Push(path);
                    }
                    else
                    {
                        var relative = NormalizeAndValidateRelativePath(Path.GetRelativePath(sourceRoot, path));
                        files.Add(new SourceFile(path, relative));
                        if (files.Count > MaximumFiles)
                        {
                            throw new InvalidOperationException("The backup exceeds its file-count limit.");
                        }
                    }
                }
            }
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static void ValidateManifest(UserDataBackupManifest? manifest)
    {
        if (manifest is null || manifest.Schema != 1 || manifest.Entries is null ||
            manifest.Entries.Count is 0 or > MaximumFiles)
        {
            throw new InvalidOperationException("The backup manifest schema or file count is invalid.");
        }

        long total = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            var normalized = NormalizeAndValidateRelativePath(entry.RelativePath);
            if (!string.Equals(normalized, entry.RelativePath, StringComparison.Ordinal) || !paths.Add(normalized) ||
                entry.Length < 0 || entry.Sha256 is not { Length: 64 } ||
                !entry.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new InvalidOperationException("A backup manifest entry is invalid or duplicated.");
            }

            if (entry.Length > MaximumTotalBytes - total)
            {
                throw new InvalidOperationException("The backup exceeds its total size limit.");
            }

            total += entry.Length;
        }
    }

    private static string NormalizeAndValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A backup path is empty.");
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (normalized.StartsWith('/') || parts.Any(part => part is "" or "." or ".." || part.Contains(':')) ||
            !IncludedRoots.Contains(parts[0], StringComparer.Ordinal))
        {
            throw new InvalidOperationException("A backup path escapes the approved user-data roots.");
        }

        return string.Join('/', parts);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A backup path escapes the restore staging directory.");
        }

        return candidate;
    }

    private static bool IsWithin(string candidate, string root)
    {
        var canonicalCandidate = Path.GetFullPath(candidate);
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateRoot(string path, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(canonical);
        if (pathRoot is not null &&
            string.Equals(
                canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A filesystem root cannot be used as the application data root.");
        }
        if (mustExist && !Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The application data root does not exist.");
        }

        if (Directory.Exists(canonical)) RejectReparsePoint(canonical, "The application data root cannot be a reparse point.");
        return canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        IncrementalHash hash,
        long remainingLimit,
        CancellationToken cancellationToken)
    {
        if (remainingLimit < 0) throw new InvalidOperationException("The backup exceeds its total size limit.");
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > remainingLimit) throw new InvalidOperationException("The backup exceeds its total size limit.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private sealed record SourceFile(string FullPath, string RelativePath);
}
