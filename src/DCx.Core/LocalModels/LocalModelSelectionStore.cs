using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public sealed record ApprovedLocalModelBinding(
    string Name,
    string Digest,
    string LicenseSha256,
    DateTimeOffset ApprovedUtc);

public sealed record LocalModelSelectionState(
    int SchemaVersion,
    ApprovedLocalModelBinding? Vision,
    ApprovedLocalModelBinding? Embedding);

public sealed class LocalModelSelectionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStateBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };

    private readonly string statePath;
    private readonly string localModelsRoot;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private LocalModelSelectionStore(string localModelsRoot)
    {
        this.localModelsRoot = localModelsRoot;
        statePath = Path.Combine(localModelsRoot, "selection.json");
    }

    public static async Task<LocalModelSelectionStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        var applicationRoot = ValidateRoot(applicationDataRoot);
        var localModelsRoot = Path.Combine(applicationRoot, "local-models");
        Directory.CreateDirectory(localModelsRoot);
        RejectReparsePoint(localModelsRoot, "The local-model settings directory cannot be a reparse point.");
        var store = new LocalModelSelectionStore(localModelsRoot);
        store.ValidateStatePathIfPresent();

        _ = await store.ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<LocalModelSelectionState> GetStateAsync(
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

    public Task SetVisionAsync(
        LocalModelDescriptor descriptor,
        string licenseSha256,
        bool explicitlyApproved,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SetAsync(descriptor, licenseSha256, explicitlyApproved, now, isVision: true, cancellationToken);

    public Task SetEmbeddingAsync(
        LocalModelDescriptor descriptor,
        string licenseSha256,
        bool explicitlyApproved,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SetAsync(descriptor, licenseSha256, explicitlyApproved, now, isVision: false, cancellationToken);

    public Task ClearVisionAsync(CancellationToken cancellationToken = default) =>
        ClearAsync(isVision: true, cancellationToken);

    public Task ClearEmbeddingAsync(CancellationToken cancellationToken = default) =>
        ClearAsync(isVision: false, cancellationToken);

    private async Task SetAsync(
        LocalModelDescriptor descriptor,
        string licenseSha256,
        bool explicitlyApproved,
        DateTimeOffset now,
        bool isVision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException("Selecting a local model requires explicit user approval.");
        }

        if (!descriptor.HasDigestBoundName || descriptor.Digest is null ||
            isVision && !descriptor.SupportsVision || !isVision && !descriptor.SupportsEmbeddings)
        {
            throw new InvalidOperationException(
                "The selected local model is not digest-bound or lacks the requested capability.");
        }

        ValidateSha256(licenseSha256, nameof(licenseSha256));
        var binding = new ApprovedLocalModelBinding(
            descriptor.Name,
            descriptor.Digest.ToLowerInvariant(),
            licenseSha256.ToLowerInvariant(),
            now.ToUniversalTime());
        ValidateBinding(binding);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var next = isVision
                ? current with { Vision = binding }
                : current with { Embedding = binding };
            await WriteUnsafeAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task ClearAsync(bool isVision, CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var next = isVision
                ? current with { Vision = null }
                : current with { Embedding = null };
            await WriteUnsafeAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task<LocalModelSelectionState> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        ValidateStatePathIfPresent();
        if (!File.Exists(statePath))
        {
            return new LocalModelSelectionState(CurrentSchemaVersion, null, null);
        }

        RejectReparsePoint(statePath, "The local-model selection file cannot be a reparse point.");
        var file = new FileInfo(statePath);
        if (file.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The local-model selection file is outside its size limit.");
        }

        try
        {
            await using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<LocalModelSelectionState>(
                    stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (state is null || state.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException("The local-model selection schema is unsupported.");
            }

            if (state.Vision is not null) ValidateBinding(state.Vision);
            if (state.Embedding is not null) ValidateBinding(state.Embedding);
            return state;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The local-model selection file is malformed.");
        }
    }

    private async Task WriteUnsafeAsync(
        LocalModelSelectionState state,
        CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (content.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The local-model selection exceeds its size limit.");
        }

        var directory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("The local-model selection path has no parent directory.");
        RejectReparsePoint(directory, "The local-model settings directory cannot be a reparse point.");
        ValidateStatePathIfPresent();
        var temporary = Path.Combine(directory, $".selection.{Guid.NewGuid():N}.tmp");
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
            }

            ValidateStatePathIfPresent();
            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateBinding(ApprovedLocalModelBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Name) || binding.Name.Length > 256 ||
            binding.Name.Any(char.IsControl))
        {
            throw new InvalidOperationException("The stored local-model name is invalid.");
        }

        if (!IsSha256(binding.Digest) || !IsSha256(binding.LicenseSha256) ||
            binding.Name.Length <= binding.Digest.Length ||
            binding.Name[binding.Name.Length - binding.Digest.Length - 1] is not (':' or '-') ||
            !binding.Name.EndsWith(binding.Digest, StringComparison.OrdinalIgnoreCase) ||
            binding.ApprovedUtc == default || binding.ApprovedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The stored local-model binding is invalid.");
        }
    }

    private static void ValidateSha256(string digest, string parameterName)
    {
        if (!IsSha256(digest))
        {
            throw new ArgumentException("A SHA-256 digest must contain 64 hexadecimal characters.", parameterName);
        }
    }

    private static bool IsSha256(string digest) =>
        !string.IsNullOrWhiteSpace(digest) && digest.Length == 64 &&
        digest.All(character => char.IsAsciiHexDigit(character));

    private static string ValidateRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var canonical = Path.GetFullPath(root);
        var pathRoot = Path.GetPathRoot(canonical);
        if (pathRoot is not null && canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The local-model application-data root cannot be a filesystem root.");
        }

        Directory.CreateDirectory(canonical);
        RejectReparsePoint(canonical, "The local-model application-data root cannot be a reparse point.");
        return canonical;
    }

    private void ValidateStatePathIfPresent()
    {
        RejectReparsePoint(localModelsRoot, "The local-model settings directory cannot be a reparse point.");
        var entries = Directory.EnumerateFileSystemEntries(localModelsRoot)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(statePath),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) return;
        if (entries.Length != 1 ||
            !string.Equals(Path.GetFileName(entries[0]), Path.GetFileName(statePath), StringComparison.Ordinal) ||
            (File.GetAttributes(entries[0]) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException("The local-model selection path is unsafe.");
        }
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(message);
        }
    }
}
