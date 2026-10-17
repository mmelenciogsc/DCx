namespace DCx.Core.Knowledge;

public enum KnowledgeResourceKind
{
    Ckb = 0,
    Ccb = 1,
}

public sealed record KnowledgeDependency(string ResourceId, string VersionConstraint);

public sealed record CcbProvenanceEnvelope(string PublisherId, string SignatureBase64);

public sealed record KnowledgeResourceDraft(
    string ResourceId,
    string Version,
    KnowledgeResourceKind Kind,
    string Title,
    string Body,
    string SourceUri,
    string License,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<KnowledgeDependency> Dependencies,
    float[]? Embedding = null,
    string? ArtifactSha256 = null,
    CcbProvenanceEnvelope? Provenance = null);

public sealed record StoredKnowledgeResource(
    string ResourceId,
    string Version,
    KnowledgeResourceKind Kind,
    string Title,
    string Body,
    string SourceUri,
    string License,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<KnowledgeDependency> Dependencies,
    string? ArtifactSha256,
    CcbProvenanceEnvelope? Provenance);

public sealed record KnowledgeSearchResult(StoredKnowledgeResource Resource, double Score);

public sealed class KnowledgeVersionConflictException(string message) : InvalidOperationException(message);

public sealed class KnowledgeArtifactException(string message) : Exception(message);
