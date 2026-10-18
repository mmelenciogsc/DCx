using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public sealed record ApprovedWhisperBackendBinding(
    string ExecutablePath,
    string ExecutableSha256,
    string ModelPath,
    string ModelSha256,
    string RuntimeLicensePath,
    string RuntimeLicenseSha256,
    string ModelLicensePath,
    string ModelLicenseSha256,
    DateTimeOffset ApprovedUtc)
{
    public WhisperCliTranscriber CreateTranscriber(TimeSpan timeout)
    {
        WhisperBackendReviewer.VerifyApprovedLicenseFile(
            RuntimeLicensePath,
            RuntimeLicenseSha256,
            "Whisper runtime license");
        WhisperBackendReviewer.VerifyApprovedLicenseFile(
            ModelLicensePath,
            ModelLicenseSha256,
            "Whisper model license");
        return new(new WhisperCliConfiguration(
            ExecutablePath,
            ExecutableSha256,
            ModelPath,
            ModelSha256,
            timeout));
    }

    public bool HasSameIdentity(ApprovedWhisperBackendBinding other) =>
        other is not null &&
        string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExecutableSha256, other.ExecutableSha256, StringComparison.Ordinal) &&
        string.Equals(ModelPath, other.ModelPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ModelSha256, other.ModelSha256, StringComparison.Ordinal) &&
        string.Equals(RuntimeLicensePath, other.RuntimeLicensePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RuntimeLicenseSha256, other.RuntimeLicenseSha256, StringComparison.Ordinal) &&
        string.Equals(ModelLicensePath, other.ModelLicensePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ModelLicenseSha256, other.ModelLicenseSha256, StringComparison.Ordinal);
}

public sealed record WhisperBackendReview(
    ApprovedWhisperBackendBinding Binding,
    string RuntimeLicenseText,
    string ModelLicenseText,
    long ExecutableBytes,
    long ModelBytes);

public sealed class WhisperBackendReviewer
{
    public const long MaximumExecutableBytes = 1024L * 1024 * 1024;
    public const long MaximumModelBytes = 16L * 1024 * 1024 * 1024;
    public const int MaximumLicenseBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<WhisperBackendReview> ReviewAsync(
        string executablePath,
        string modelPath,
        string runtimeLicensePath,
        string modelLicensePath,
        DateTimeOffset reviewedUtc,
        CancellationToken cancellationToken = default)
    {
        var executable = ValidatePath(executablePath, "Whisper executable", [".exe"]);
        var model = ValidatePath(modelPath, "Whisper model", [".bin", ".gguf"]);
        var runtimeLicense = ValidateLicensePath(runtimeLicensePath, "Whisper runtime license");
        var modelLicense = ValidateLicensePath(modelLicensePath, "Whisper model license");
        var paths = new[] { executable, model, runtimeLicense, modelLicense };
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw new ArgumentException("Whisper executable, model and license files must be distinct.");
        }

        await using var executableLease = OpenLease(executable, MaximumExecutableBytes, "Whisper executable");
        await using var modelLease = OpenLease(model, MaximumModelBytes, "Whisper model");
        await using var runtimeLicenseLease = OpenLease(runtimeLicense, MaximumLicenseBytes, "Whisper runtime license");
        await using var modelLicenseLease = OpenLease(modelLicense, MaximumLicenseBytes, "Whisper model license");

        var executableDigest = HashAsync(executableLease, cancellationToken);
        var modelDigest = HashAsync(modelLease, cancellationToken);
        var runtimeLicenseBytes = ReadAllAsync(runtimeLicenseLease, cancellationToken);
        var modelLicenseBytes = ReadAllAsync(modelLicenseLease, cancellationToken);
        await Task.WhenAll(executableDigest, modelDigest, runtimeLicenseBytes, modelLicenseBytes)
            .ConfigureAwait(false);

        var runtimeBytes = await runtimeLicenseBytes.ConfigureAwait(false);
        var modelBytes = await modelLicenseBytes.ConfigureAwait(false);
        var runtimeLicenseDigest = Convert.ToHexStringLower(SHA256.HashData(runtimeBytes));
        var modelLicenseDigest = Convert.ToHexStringLower(SHA256.HashData(modelBytes));
        var runtimeText = DecodeLicense(runtimeBytes, "Whisper runtime license");
        var modelText = DecodeLicense(modelBytes, "Whisper model license");
        var binding = new ApprovedWhisperBackendBinding(
            executable,
            await executableDigest.ConfigureAwait(false),
            model,
            await modelDigest.ConfigureAwait(false),
            runtimeLicense,
            runtimeLicenseDigest,
            modelLicense,
            modelLicenseDigest,
            reviewedUtc.ToUniversalTime());
        WhisperBackendSelectionStore.ValidateBinding(binding);
        return new WhisperBackendReview(
            binding,
            runtimeText,
            modelText,
            executableLease.Length,
            modelLease.Length);
    }

    private static string ValidatePath(string path, string label, IReadOnlyCollection<string> extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path.Contains('\0')) throw new ArgumentException($"{label} path is invalid.", nameof(path));
        var canonical = Path.GetFullPath(path.Trim());
        RejectNetworkPath(canonical, label);
        if (!extensions.Any(extension =>
                string.Equals(extension, Path.GetExtension(canonical), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"{label} has an unsupported file extension.", nameof(path));
        }

        RejectReparseChain(canonical, label);
        return canonical;
    }

    private static string ValidateLicensePath(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path.Contains('\0')) throw new ArgumentException($"{label} path is invalid.", nameof(path));
        var canonical = Path.GetFullPath(path.Trim());
        RejectNetworkPath(canonical, label);
        var name = Path.GetFileName(canonical);
        var extension = Path.GetExtension(canonical);
        if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{label} must be a text, Markdown, LICENSE or NOTICE file.", nameof(path));
        }

        RejectReparseChain(canonical, label);
        return canonical;
    }

    private static void RejectReparseChain(string path, string label)
    {
        var current = new FileInfo(path);
        if (!current.Exists || (current.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new ArgumentException($"{label} must be an existing regular file.", nameof(path));
        }

        DirectoryInfo? parent = current.Directory;
        while (parent is not null)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException($"{label} cannot cross a reparse directory.", nameof(path));
            }
            parent = parent.Parent;
        }
    }

    private static void RejectNetworkPath(string path, string label)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{label} must be on a local drive.", nameof(path));
        }
    }

    private static FileStream OpenLease(string path, long maximumBytes, string label)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            stream.Dispose();
            throw new ArgumentException($"{label} is empty or exceeds its size limit.", nameof(path));
        }
        return stream;
    }

    private static async Task<string> HashAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static async Task<byte[]> ReadAllAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static string DecodeLicense(byte[] bytes, string label)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes)
                .TrimStart('\uFEFF')
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"{label} is not valid UTF-8 text.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        if (string.IsNullOrWhiteSpace(text) || text.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new InvalidOperationException($"{label} is empty or contains unsupported control characters.");
        }
        return text;
    }

    internal static void VerifyApprovedLicenseFile(string path, string expectedSha256, string label)
    {
        WhisperBackendSelectionStore.ValidateDigestValue(expectedSha256);
        var canonical = ValidateLicensePath(path, label);
        using var stream = OpenLease(canonical, MaximumLicenseBytes, label);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedSha256)))
        {
            throw new InvalidOperationException($"The approved {label} digest no longer matches.");
        }
    }
}

public sealed record WhisperBackendSelectionState(
    int SchemaVersion,
    ApprovedWhisperBackendBinding? Backend);

public sealed class WhisperBackendSelectionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStateBytes = 32 * 1024;
    private const string ProtectedStateKind = "whisper-selection";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };

    private readonly string root;
    private readonly string statePath;
    private readonly string storageId;
    private readonly ILocalModelApprovalStateAnchorStore anchorStore;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private WhisperBackendSelectionStore(
        string root,
        ILocalModelApprovalStateAnchorStore anchorStore)
    {
        this.root = root;
        this.anchorStore = anchorStore;
        statePath = Path.Combine(root, "transcription.json");
        storageId = LocalModelApprovalStateProtector.StorageId(statePath);
    }

    public bool RequiresReapproval { get; private set; }

    public static async Task<WhisperBackendSelectionStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default) =>
        await OpenWithAnchorAsync(
            applicationDataRoot,
            new WindowsLocalModelApprovalStateAnchorStore(),
            cancellationToken).ConfigureAwait(false);

    public static async Task<WhisperBackendSelectionStore> OpenWithAnchorAsync(
        string applicationDataRoot,
        ILocalModelApprovalStateAnchorStore anchorStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchorStore);
        var applicationRoot = LocalModelStoragePathGuard.EnsureDirectory(
            applicationDataRoot,
            "Whisper application-data");
        var root = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(applicationRoot, "local-models"),
            "Whisper settings");
        var store = new WhisperBackendSelectionStore(root, anchorStore);
        store.ValidateStatePath();
        _ = await store.ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<WhisperBackendSelectionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false); }
        finally { mutationLock.Release(); }
    }

    public async Task SetAsync(
        ApprovedWhisperBackendBinding binding,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!explicitlyApproved) throw new InvalidOperationException("Selecting a Whisper backend requires explicit approval.");
        ValidateBinding(binding);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(
                new WhisperBackendSelectionState(CurrentSchemaVersion, binding),
                cancellationToken).ConfigureAwait(false);
        }
        finally { mutationLock.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(
                new WhisperBackendSelectionState(CurrentSchemaVersion, null),
                cancellationToken).ConfigureAwait(false);
        }
        finally { mutationLock.Release(); }
    }

    internal static void ValidateBinding(ApprovedWhisperBackendBinding binding)
    {
        ValidateStoredPath(binding.ExecutablePath, ".exe", nameof(binding.ExecutablePath));
        var modelExtension = Path.GetExtension(binding.ModelPath);
        if (!string.Equals(modelExtension, ".bin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(modelExtension, ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The stored Whisper model path is invalid.");
        }
        ValidateStoredPath(binding.ModelPath, modelExtension, nameof(binding.ModelPath));
        ValidateStoredLicensePath(binding.RuntimeLicensePath, nameof(binding.RuntimeLicensePath));
        ValidateStoredLicensePath(binding.ModelLicensePath, nameof(binding.ModelLicensePath));
        var paths = new[]
        {
            binding.ExecutablePath,
            binding.ModelPath,
            binding.RuntimeLicensePath,
            binding.ModelLicensePath,
        };
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw new InvalidOperationException("Stored Whisper executable, model and license paths must be distinct.");
        }
        ValidateDigestValue(binding.ExecutableSha256);
        ValidateDigestValue(binding.ModelSha256);
        ValidateDigestValue(binding.RuntimeLicenseSha256);
        ValidateDigestValue(binding.ModelLicenseSha256);
        if (binding.ApprovedUtc == default || binding.ApprovedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The stored Whisper approval time is invalid.");
        }
    }

    private async Task<WhisperBackendSelectionState> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        ValidateStatePath();
        if (!File.Exists(statePath)) return new WhisperBackendSelectionState(CurrentSchemaVersion, null);
        LocalModelStoragePathGuard.ValidateFile(statePath, mustExist: true, "Whisper selection");
        var file = new FileInfo(statePath);
        if (file.Length is <= 0 or > MaximumStateBytes) throw new InvalidOperationException("The Whisper selection file is outside its size limit.");
        var content = new byte[checked((int)file.Length)];
        try
        {
            await using (var stream = new FileStream(
                             statePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            }
            var state = JsonSerializer.Deserialize<WhisperBackendSelectionState>(content, JsonOptions);
            if (state is null || state.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The Whisper selection schema is unsupported.");
            }
            if (state.Backend is not null) ValidateBinding(state.Backend);
            if (state.Backend is not null &&
                !await LocalModelApprovalStateProtector.MatchesAsync(
                    ProtectedStateKind,
                    storageId,
                    content,
                    anchorStore,
                    cancellationToken).ConfigureAwait(false))
            {
                RequiresReapproval = true;
                return new WhisperBackendSelectionState(CurrentSchemaVersion, null);
            }
            RequiresReapproval = false;
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Whisper selection file is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private async Task WriteUnsafeAsync(WhisperBackendSelectionState state, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (content.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The Whisper selection exceeds its size limit.");
        }

        LocalModelStoragePathGuard.RequireDirectory(root, "Whisper settings");
        ValidateStatePath();
        var removesAllAuthority = state.Backend is null;
        if (removesAllAuthority)
        {
            await anchorStore.DeleteAsync(
                ProtectedStateKind,
                storageId,
                cancellationToken).ConfigureAwait(false);
        }
        var temporary = Path.Combine(root, $"transcription-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            LocalModelStoragePathGuard.RequireDirectory(root, "Whisper settings");
            LocalModelStoragePathGuard.ValidateFile(temporary, mustExist: true, "Whisper temporary selection");
            ValidateStatePath();
            File.Move(temporary, statePath, overwrite: true);
            if (!removesAllAuthority)
            {
                await LocalModelApprovalStateProtector.ProtectAsync(
                    ProtectedStateKind,
                    storageId,
                    content,
                    anchorStore,
                    cancellationToken).ConfigureAwait(false);
            }
            RequiresReapproval = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            LocalModelStoragePathGuard.TryDeleteTemporary(root, temporary, "Whisper temporary selection");
        }
    }

    private void ValidateStatePath()
    {
        LocalModelStoragePathGuard.RequireDirectory(root, "Whisper settings");
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(statePath),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length > 1 || entries.Length == 1 &&
            (!string.Equals(Path.GetFileName(entries[0]), Path.GetFileName(statePath), StringComparison.Ordinal) ||
             (File.GetAttributes(entries[0]) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
        {
            throw new InvalidOperationException("The Whisper selection path is unsafe.");
        }
        if (entries.Length == 1)
        {
            LocalModelStoragePathGuard.ValidateFile(statePath, mustExist: true, "Whisper selection");
        }
    }

    private static void ValidateStoredPath(string path, string? extension, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) || path.Contains('\0') ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            extension is not null && !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The stored {label} is invalid.");
        }
    }

    private static void ValidateStoredLicensePath(string path, string label)
    {
        ValidateStoredPath(path, null, label);
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The stored {label} is not a supported license file.");
        }
    }

    internal static void ValidateDigestValue(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 ||
            !digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidOperationException("A stored Whisper digest is invalid.");
        }
    }
}
