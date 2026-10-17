using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCx.Core.Contributions;

public static class ContributionCodec
{
    public static string CandidateSha256(ContributionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("candidateId", candidate.CandidateId);
            writer.WriteString("resourceId", candidate.ResourceId);
            writer.WriteString("version", candidate.Version);
            writer.WriteNumber("kind", (int)candidate.Kind);
            writer.WriteString("title", candidate.Title);
            writer.WriteString("bodySha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Body))));
            writer.WriteString("sourceUri", candidate.SourceUri);
            writer.WriteString("license", candidate.License);
            writer.WriteString("contributorPseudonym", candidate.ContributorPseudonym);
            if (candidate.ArtifactSha256 is null)
            {
                writer.WriteNull("artifactSha256");
            }
            else
            {
                writer.WriteString("artifactSha256", candidate.ArtifactSha256.ToLowerInvariant());
            }

            writer.WriteStartArray("capabilities");
            foreach (var capability in candidate.Capabilities.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("derivedFrom");
            foreach (var edge in candidate.DerivedFrom.OrderBy(item => item.ParentResourceId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("parentResourceId", edge.ParentResourceId);
                writer.WriteNumber("weight", edge.Weight);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
