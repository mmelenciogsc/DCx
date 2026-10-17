using System.Security.Cryptography;
using System.Text.Json;

namespace DCx.Core.Knowledge;

public sealed record TrustedCcbPublisher(string PublisherId, byte[] SubjectPublicKeyInfo);

public sealed class CcbProvenanceVerifier(IEnumerable<TrustedCcbPublisher> trustedPublishers)
{
    private readonly IReadOnlyDictionary<string, byte[]> trustedKeys = trustedPublishers
        .ToDictionary(
            publisher => publisher.PublisherId,
            publisher => publisher.SubjectPublicKeyInfo.ToArray(),
            StringComparer.Ordinal);

    public void Verify(KnowledgeResourceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind != KnowledgeResourceKind.Ccb)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.ArtifactSha256) || draft.Provenance is null)
        {
            throw new KnowledgeArtifactException("A CCB resource requires an artifact digest and signed provenance.");
        }

        if (!trustedKeys.TryGetValue(draft.Provenance.PublisherId, out var publicKey))
        {
            throw new KnowledgeArtifactException("The CCB publisher is not trusted by this private store.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(draft.Provenance.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new KnowledgeArtifactException($"The CCB provenance signature is malformed: {exception.GetType().Name}.");
        }

        try
        {
            using var signer = ECDsa.Create();
            signer.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length ||
                !signer.VerifyData(CreatePayload(draft), signature, HashAlgorithmName.SHA256))
            {
                throw new KnowledgeArtifactException("The CCB provenance signature is invalid.");
            }
        }
        catch (CryptographicException exception)
        {
            throw new KnowledgeArtifactException($"The CCB publisher key is invalid: {exception.GetType().Name}.");
        }
    }

    public static byte[] CreatePayload(KnowledgeResourceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("resourceId", draft.ResourceId);
            writer.WriteString("version", draft.Version);
            writer.WriteString("title", draft.Title);
            writer.WriteString("bodySha256", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(draft.Body))));
            writer.WriteString("sourceUri", draft.SourceUri);
            writer.WriteString("license", draft.License);
            writer.WriteString("artifactSha256", draft.ArtifactSha256);
            writer.WriteStartArray("capabilities");
            foreach (var capability in draft.Capabilities.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("dependencies");
            foreach (var dependency in draft.Dependencies
                         .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
                         .ThenBy(item => item.VersionConstraint, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("resourceId", dependency.ResourceId);
                writer.WriteString("versionConstraint", dependency.VersionConstraint);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
