using DCx.Core.Knowledge;

namespace DCx.Core.GlobalKnowledge;

public enum GlobalReleaseChangeKind
{
    Upsert = 0,
    Deprecate = 1,
    Remove = 2,
}

public sealed record GlobalArtifactReference(string Sha256, long SizeBytes, string MediaType);

public sealed record GlobalResourceRecord(
    string ResourceId,
    string Version,
    KnowledgeResourceKind Kind,
    string Title,
    string Summary,
    string SourceUri,
    string License,
    IReadOnlyList<string> Capabilities,
    float[]? Embedding = null,
    GlobalArtifactReference? Artifact = null,
    bool Deprecated = false);

public sealed record GlobalReleaseChange(
    GlobalReleaseChangeKind Kind,
    string ResourceId,
    string Version,
    GlobalResourceRecord? Resource = null);

public sealed record GlobalReleaseManifest(
    int SchemaVersion,
    long Sequence,
    long PreviousSequence,
    string? PreviousReleaseSha256,
    string ResultCatalogSha256,
    DateTimeOffset IssuedUtc,
    IReadOnlyList<GlobalReleaseChange> Changes);

public sealed record SignedGlobalRelease(GlobalReleaseManifest Manifest, string SignatureBase64);

public sealed record GlobalReleaseDescriptor(long Sequence, string Sha256, long SizeBytes);

public sealed record GlobalReplicaStatus(long Sequence, string? ReleaseSha256, int ResourceCount);

public sealed record GlobalSyncResult(
    GlobalReplicaStatus Status,
    int ReleasesApplied,
    long BytesDownloaded,
    bool WasAlreadyCurrent);

public interface IGlobalRegistryTransport
{
    Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken);

    Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken);

    Task<byte[]> DownloadReleaseChunkAsync(
        long sequence,
        long offset,
        int count,
        CancellationToken cancellationToken);

    Task<byte[]> DownloadArtifactChunkAsync(
        string sha256,
        long offset,
        int count,
        CancellationToken cancellationToken);
}

public sealed class GlobalReplicaException(string message) : Exception(message);
