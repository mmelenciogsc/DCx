using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Knowledge;

namespace DCx.Core.Contributions;

public sealed record ContributionTransportEnvelope(
    PreparedContribution Submission,
    byte[] Artifact);

public static class ContributionTransportCodec
{
    public const string MediaType = "application/vnd.dcx.contribution.v1+binary";
    public const int MaximumMetadataBytes = 1024 * 1024;
    public const int HeaderBytes = 9;
    public const int MaximumEnvelopeBytes =
        HeaderBytes + MaximumMetadataBytes + ContentAddressedArtifactStore.MaximumArtifactBytes;

    private static ReadOnlySpan<byte> Magic => "DCXC"u8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static byte[] Encode(
        PreparedContribution submission,
        ReadOnlySpan<byte> artifact = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (artifact.Length > ContentAddressedArtifactStore.MaximumArtifactBytes)
        {
            throw new ContributionPolicyException("contribution_envelope_size_invalid");
        }

        var metadata = JsonSerializer.SerializeToUtf8Bytes(submission, JsonOptions);
        if (metadata.Length is <= 0 or > MaximumMetadataBytes)
        {
            throw new ContributionPolicyException("contribution_envelope_size_invalid");
        }

        var result = GC.AllocateUninitializedArray<byte>(HeaderBytes + metadata.Length + artifact.Length);
        Magic.CopyTo(result);
        result[4] = 1;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(5, 4), metadata.Length);
        metadata.CopyTo(result.AsSpan(HeaderBytes));
        artifact.CopyTo(result.AsSpan(HeaderBytes + metadata.Length));
        return result;
    }

    public static ContributionTransportEnvelope Decode(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length is <= HeaderBytes or > MaximumEnvelopeBytes ||
            !envelope[..4].SequenceEqual(Magic) || envelope[4] != 1)
        {
            throw new ContributionPolicyException("contribution_envelope_invalid");
        }

        var metadataLength = BinaryPrimitives.ReadInt32BigEndian(envelope.Slice(5, 4));
        if (metadataLength is <= 0 or > MaximumMetadataBytes ||
            HeaderBytes + metadataLength > envelope.Length)
        {
            throw new ContributionPolicyException("contribution_envelope_invalid");
        }

        try
        {
            var submission = JsonSerializer.Deserialize<PreparedContribution>(
                    envelope.Slice(HeaderBytes, metadataLength), JsonOptions)
                ?? throw new ContributionPolicyException("contribution_envelope_invalid");
            var artifact = envelope[(HeaderBytes + metadataLength)..].ToArray();
            if (artifact.Length > ContentAddressedArtifactStore.MaximumArtifactBytes)
            {
                throw new ContributionPolicyException("contribution_envelope_size_invalid");
            }

            return new ContributionTransportEnvelope(submission, artifact);
        }
        catch (ContributionPolicyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new ContributionPolicyException("contribution_envelope_invalid");
        }
    }
}
