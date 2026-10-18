using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Security;

namespace DCx.Core.LocalModels;

public sealed record WhisperRuntimeStageAnchor(
    int SchemaVersion,
    string CandidateId,
    string ArchiveSha256,
    string ReceiptSha256,
    long ReceiptBytes,
    DateTimeOffset StagedUtc);

public interface IWhisperRuntimeStageAnchorStore
{
    Task<WhisperRuntimeStageAnchor?> ReadAsync(
        PinnedLocalModelDependencyCandidate candidate,
        CancellationToken cancellationToken = default);

    Task BindAsync(
        WhisperRuntimeStageAnchor anchor,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsWhisperRuntimeStageAnchorStore : IWhisperRuntimeStageAnchorStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumAnchorCharacters = 2 * 1024;
    private const string TargetPrefix = "DCx/LocalModels/WhisperRuntimeStage/v1/";

    private static readonly object VaultLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly WindowsCredentialVault vault;

    public WindowsWhisperRuntimeStageAnchorStore()
        : this(new WindowsCredentialVault())
    {
    }

    internal WindowsWhisperRuntimeStageAnchorStore(WindowsCredentialVault vault) =>
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));

    public Task<WhisperRuntimeStageAnchor?> ReadAsync(
        PinnedLocalModelDependencyCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRuntimeCandidate(candidate);
        lock (VaultLock)
        {
            return Task.FromResult(ReadCore(candidate.Id, candidate.Sha256));
        }
    }

    public Task BindAsync(
        WhisperRuntimeStageAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnchor(anchor);
        var normalized = anchor with
        {
            ArchiveSha256 = anchor.ArchiveSha256.ToLowerInvariant(),
            ReceiptSha256 = anchor.ReceiptSha256.ToLowerInvariant(),
        };
        lock (VaultLock)
        {
            var current = ReadCore(normalized.CandidateId, normalized.ArchiveSha256);
            if (current is not null)
            {
                if (current == normalized) return Task.CompletedTask;
                throw new InvalidOperationException(
                    "The protected Whisper runtime stage anchor already contains a conflicting receipt identity.");
            }

            try
            {
                vault.Save(TargetName(normalized.CandidateId, normalized.ArchiveSha256),
                    JsonSerializer.Serialize(normalized, JsonOptions));
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    $"The protected Whisper runtime stage anchor could not be written: {exception.GetType().Name}.");
            }
        }
        return Task.CompletedTask;
    }

    internal static string TargetName(string candidateId, string archiveSha256)
    {
        LocalModelDependencyCatalogLoader.ValidateIdentifier(candidateId, "stage anchor");
        LocalModelDependencyCatalogLoader.ValidateSha256(archiveSha256, "stage anchor archive");
        var identityBytes = Encoding.UTF8.GetBytes(candidateId + "\n" + archiveSha256);
        try
        {
            return TargetPrefix + Convert.ToHexStringLower(SHA256.HashData(identityBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityBytes);
        }
    }

    private WhisperRuntimeStageAnchor? ReadCore(string candidateId, string archiveSha256)
    {
        string? value;
        try
        {
            value = vault.Read(TargetName(candidateId, archiveSha256));
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"The protected Whisper runtime stage anchor could not be read: {exception.GetType().Name}.");
        }
        if (value is null) return null;
        if (value.Length is <= 0 or > MaximumAnchorCharacters || value.Contains('\0'))
        {
            throw new InvalidOperationException("The protected Whisper runtime stage anchor is outside its limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            RejectDuplicateProperties(document.RootElement);
            var anchor = JsonSerializer.Deserialize<WhisperRuntimeStageAnchor>(value, JsonOptions)
                ?? throw new InvalidOperationException("The protected Whisper runtime stage anchor is empty.");
            ValidateAnchor(anchor);
            if (!string.Equals(anchor.CandidateId, candidateId, StringComparison.Ordinal) ||
                !string.Equals(anchor.ArchiveSha256, archiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The protected Whisper runtime stage anchor belongs to another candidate.");
            }
            return anchor with
            {
                ArchiveSha256 = anchor.ArchiveSha256.ToLowerInvariant(),
                ReceiptSha256 = anchor.ReceiptSha256.ToLowerInvariant(),
            };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The protected Whisper runtime stage anchor is malformed.",
                exception);
        }
    }

    private static void ValidateRuntimeCandidate(PinnedLocalModelDependencyCandidate candidate)
    {
        LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
        if (candidate.Kind != LocalModelDependencyKind.WhisperRuntimeArchive)
        {
            throw new InvalidOperationException("Only a pinned Whisper runtime may have a stage anchor.");
        }
    }

    private static void ValidateAnchor(WhisperRuntimeStageAnchor anchor)
    {
        if (anchor.SchemaVersion != CurrentSchemaVersion ||
            anchor.ReceiptBytes is <= 0 or > WhisperRuntimeStager.MaximumReceiptBytes ||
            anchor.StagedUtc == default || anchor.StagedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The protected Whisper runtime stage anchor is invalid.");
        }
        LocalModelDependencyCatalogLoader.ValidateIdentifier(anchor.CandidateId, "stage anchor");
        LocalModelDependencyCatalogLoader.ValidateSha256(anchor.ArchiveSha256, "stage anchor archive");
        LocalModelDependencyCatalogLoader.ValidateSha256(anchor.ReceiptSha256, "stage anchor receipt");
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
                    throw new InvalidOperationException(
                        "The protected Whisper runtime stage anchor has duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
