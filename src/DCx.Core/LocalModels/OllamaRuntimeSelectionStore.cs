using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public sealed record ApprovedOllamaRuntimeConfiguration(
    ApprovedOllamaRuntimeBinding Runtime,
    string ModelRoot,
    DateTimeOffset ApprovedUtc)
{
    public bool HasSameIdentity(ApprovedOllamaRuntimeConfiguration other) =>
        other is not null &&
        string.Equals(Runtime.RuntimeRoot, other.Runtime.RuntimeRoot, PathComparison) &&
        string.Equals(Runtime.RuntimeProfile, other.Runtime.RuntimeProfile, StringComparison.Ordinal) &&
        string.Equals(Runtime.ExecutableRelativePath, other.Runtime.ExecutableRelativePath, StringComparison.Ordinal) &&
        string.Equals(Runtime.TreeSha256, other.Runtime.TreeSha256, StringComparison.Ordinal) &&
        string.Equals(Runtime.AuthenticodeStatus, other.Runtime.AuthenticodeStatus, StringComparison.Ordinal) &&
        string.Equals(Runtime.SignerSubject, other.Runtime.SignerSubject, StringComparison.Ordinal) &&
        string.Equals(Runtime.SignerCertificateSha256, other.Runtime.SignerCertificateSha256, StringComparison.Ordinal) &&
        string.Equals(ModelRoot, other.ModelRoot, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed record OllamaRuntimeSelectionState(
    int SchemaVersion,
    ApprovedOllamaRuntimeConfiguration? Runtime);

public sealed class OllamaRuntimeSelectionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStateBytes = 60 * 1024;
    private const string ProtectedStateKind = "ollama-runtime-selection";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 12,
    };

    private readonly string root;
    private readonly string statePath;
    private readonly string storageId;
    private readonly ILocalModelApprovalStateAnchorStore anchorStore;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private OllamaRuntimeSelectionStore(
        string root,
        ILocalModelApprovalStateAnchorStore anchorStore)
    {
        this.root = root;
        this.anchorStore = anchorStore;
        statePath = Path.Combine(root, "ollama-runtime.json");
        storageId = LocalModelApprovalStateProtector.StorageId(statePath);
    }

    public bool RequiresReapproval { get; private set; }

    public static async Task<OllamaRuntimeSelectionStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default) =>
        await OpenWithAnchorAsync(
            applicationDataRoot,
            new WindowsLocalModelApprovalStateAnchorStore(),
            cancellationToken).ConfigureAwait(false);

    public static async Task<OllamaRuntimeSelectionStore> OpenWithAnchorAsync(
        string applicationDataRoot,
        ILocalModelApprovalStateAnchorStore anchorStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchorStore);
        var applicationRoot = LocalModelStoragePathGuard.EnsureDirectory(
            applicationDataRoot,
            "Ollama runtime application-data");
        var root = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(applicationRoot, "local-models"),
            "Ollama runtime settings");
        var store = new OllamaRuntimeSelectionStore(root, anchorStore);
        _ = await store.ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<OllamaRuntimeSelectionState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task SetAsync(
        OllamaRuntimeReview review,
        string modelRoot,
        bool explicitlyApproved,
        DateTimeOffset approvedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException(
                "Selecting an Ollama runtime requires explicit user approval.");
        }
        if (!string.Equals(review.OfficialLicenseUri, OllamaRuntimeReviewer.OfficialLicenseUri, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Ollama runtime license reference is not the reviewed official reference.");
        }
        if (!string.Equals(review.Binding.AuthenticodeStatus, "Valid", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only a runtime with a valid Windows publisher signature can be selected.");
        }

        var configuration = new ApprovedOllamaRuntimeConfiguration(
            review.Binding,
            ValidateModelRoot(modelRoot),
            approvedUtc.ToUniversalTime());
        ValidateConfiguration(configuration);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(
                new OllamaRuntimeSelectionState(CurrentSchemaVersion, configuration),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(
                new OllamaRuntimeSelectionState(CurrentSchemaVersion, null),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<OllamaOwnedRuntime> StartApprovedAsync(
        TimeSpan startupTimeout,
        CancellationToken cancellationToken = default)
    {
        var configuration = (await GetStateAsync(cancellationToken).ConfigureAwait(false)).Runtime;
        if (configuration is null)
        {
            throw new InvalidOperationException(
                "No protected, explicitly approved Ollama runtime is selected.");
        }
        ValidateConfiguration(configuration);
        return await new OllamaOwnedRuntimeLauncher().StartAsync(
            configuration.Runtime,
            new OllamaOwnedRuntimeOptions(configuration.ModelRoot, startupTimeout),
            explicitlyApproved: true,
            cancellationToken).ConfigureAwait(false);
    }

    public static string ValidateModelRoot(string modelRoot)
    {
        if (string.IsNullOrWhiteSpace(modelRoot) || modelRoot.Length > 1024 || modelRoot.Contains('\0'))
        {
            throw new ArgumentException("The Ollama model root is invalid.", nameof(modelRoot));
        }
        var canonical = Path.GetFullPath(modelRoot.Trim());
        var pathRoot = Path.GetPathRoot(canonical);
        if (!Path.IsPathFullyQualified(canonical) || canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Directory.Exists(canonical) || string.IsNullOrWhiteSpace(pathRoot) ||
            string.Equals(
                canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                PathComparison))
        {
            throw new ArgumentException(
                "The Ollama model root must be an existing non-root local directory.",
                nameof(modelRoot));
        }
        for (var current = new DirectoryInfo(canonical); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The Ollama model root cannot cross a reparse point.");
            }
        }
        return canonical;
    }

    internal static void ValidateConfiguration(ApprovedOllamaRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        OllamaRuntimeReviewer.ValidateBinding(configuration.Runtime);
        if (!string.Equals(configuration.Runtime.AuthenticodeStatus, "Valid", StringComparison.Ordinal) ||
            configuration.ApprovedUtc == default || configuration.ApprovedUtc.Offset != TimeSpan.Zero ||
            configuration.ApprovedUtc < configuration.Runtime.ReviewedUtc ||
            configuration.ApprovedUtc - configuration.Runtime.ReviewedUtc > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("The stored Ollama runtime approval is invalid.");
        }
        var canonicalModelRoot = ValidateModelRoot(configuration.ModelRoot);
        if (!string.Equals(canonicalModelRoot, configuration.ModelRoot, PathComparison))
        {
            throw new InvalidOperationException("The stored Ollama model root is not canonical.");
        }
    }

    private async Task<OllamaRuntimeSelectionState> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        ValidateStatePath();
        if (!File.Exists(statePath)) return new(CurrentSchemaVersion, null);
        LocalModelStoragePathGuard.ValidateFile(statePath, mustExist: true, "Ollama runtime selection");
        var file = new FileInfo(statePath);
        if (file.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The Ollama runtime selection file is outside its size limit.");
        }

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
            var state = JsonSerializer.Deserialize<OllamaRuntimeSelectionState>(content, JsonOptions);
            if (state is null || state.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The Ollama runtime selection schema is unsupported.");
            }
            if (state.Runtime is not null) ValidateConfiguration(state.Runtime);
            if (state.Runtime is not null &&
                !await LocalModelApprovalStateProtector.MatchesAsync(
                    ProtectedStateKind,
                    storageId,
                    content,
                    anchorStore,
                    cancellationToken).ConfigureAwait(false))
            {
                RequiresReapproval = true;
                return new OllamaRuntimeSelectionState(CurrentSchemaVersion, null);
            }
            RequiresReapproval = false;
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Ollama runtime selection file is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private async Task WriteUnsafeAsync(
        OllamaRuntimeSelectionState state,
        CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (content.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The Ollama runtime selection exceeds its size limit.");
        }
        LocalModelStoragePathGuard.RequireDirectory(root, "Ollama runtime settings");
        ValidateStatePath();
        var removesAllAuthority = state.Runtime is null;
        if (removesAllAuthority)
        {
            await anchorStore.DeleteAsync(
                ProtectedStateKind,
                storageId,
                cancellationToken).ConfigureAwait(false);
        }
        var temporary = Path.Combine(root, $"ollama-runtime-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            LocalModelStoragePathGuard.RequireDirectory(root, "Ollama runtime settings");
            LocalModelStoragePathGuard.ValidateFile(
                temporary,
                mustExist: true,
                "Ollama runtime temporary selection");
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
            LocalModelStoragePathGuard.TryDeleteTemporary(
                root,
                temporary,
                "Ollama runtime temporary selection");
        }
    }

    private void ValidateStatePath()
    {
        LocalModelStoragePathGuard.RequireDirectory(root, "Ollama runtime settings");
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
            throw new InvalidOperationException("The Ollama runtime selection path is unsafe.");
        }
        if (entries.Length == 1)
        {
            LocalModelStoragePathGuard.ValidateFile(
                statePath,
                mustExist: true,
                "Ollama runtime selection");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
