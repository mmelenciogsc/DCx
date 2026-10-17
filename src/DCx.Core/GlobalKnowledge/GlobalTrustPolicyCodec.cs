using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.GlobalKnowledge;

public static class GlobalTrustPolicyCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPolicyBytes = 128 * 1024;
    public const int MaximumReleaseKeys = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static byte[] CreateCanonicalPolicy(GlobalTrustPolicy policy)
    {
        Validate(policy);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", policy.SchemaVersion);
            writer.WriteNumber("revision", policy.Revision);
            if (policy.PreviousPolicySha256 is null)
            {
                writer.WriteNull("previousPolicySha256");
            }
            else
            {
                writer.WriteString("previousPolicySha256", policy.PreviousPolicySha256.ToLowerInvariant());
            }

            writer.WriteString("issuedUtc", policy.IssuedUtc.ToUniversalTime().ToString("O"));
            writer.WriteStartArray("releaseKeys");
            foreach (var key in policy.ReleaseKeys.OrderBy(item => item.KeyId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("keyId", key.KeyId.ToLowerInvariant());
                writer.WriteString("subjectPublicKeyInfoBase64", key.SubjectPublicKeyInfoBase64);
                writer.WriteNumber("validFromSequence", key.ValidFromSequence);
                if (key.ValidThroughSequence is null)
                {
                    writer.WriteNull("validThroughSequence");
                }
                else
                {
                    writer.WriteNumber("validThroughSequence", key.ValidThroughSequence.Value);
                }

                if (key.RevokedFromSequence is null)
                {
                    writer.WriteNull("revokedFromSequence");
                }
                else
                {
                    writer.WriteNumber("revokedFromSequence", key.RevokedFromSequence.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] Encode(SignedGlobalTrustPolicy signedPolicy)
    {
        ArgumentNullException.ThrowIfNull(signedPolicy);
        var policy = CreateCanonicalPolicy(signedPolicy.Policy);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signedPolicy.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new GlobalReplicaException($"The trust-policy signature is malformed: {exception.GetType().Name}.");
        }

        if (signature.Length == 0)
        {
            throw new GlobalReplicaException("The trust-policy signature is empty.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(Convert.ToBase64String(policy), Convert.ToBase64String(signature)),
            JsonOptions);
    }

    public static SignedGlobalTrustPolicy Decode(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumPolicyBytes)
        {
            throw new GlobalReplicaException("The trust-policy envelope is empty or too large.");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes, JsonOptions)
                ?? throw new GlobalReplicaException("The trust-policy envelope is empty.");
            var policyBytes = Convert.FromBase64String(envelope.PolicyBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            if (policyBytes.Length == 0 || signature.Length == 0)
            {
                throw new GlobalReplicaException("The trust-policy envelope has an empty policy or signature.");
            }

            var policy = JsonSerializer.Deserialize<GlobalTrustPolicy>(policyBytes, JsonOptions)
                ?? throw new GlobalReplicaException("The trust policy is empty.");
            Validate(policy);
            if (!policyBytes.AsSpan().SequenceEqual(CreateCanonicalPolicy(policy)))
            {
                throw new GlobalReplicaException("The trust policy is not in canonical form.");
            }

            return new SignedGlobalTrustPolicy(policy, Convert.ToBase64String(signature));
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new GlobalReplicaException($"The trust-policy envelope is malformed: {exception.GetType().Name}.");
        }
    }

    public static SignedGlobalTrustPolicy Sign(GlobalTrustPolicy policy, ECDsa trustRoot)
    {
        ArgumentNullException.ThrowIfNull(trustRoot);
        var signature = trustRoot.SignData(CreateCanonicalPolicy(policy), HashAlgorithmName.SHA256);
        return new SignedGlobalTrustPolicy(policy, Convert.ToBase64String(signature));
    }

    public static void Verify(SignedGlobalTrustPolicy signedPolicy, ReadOnlySpan<byte> trustRootSubjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(signedPolicy);
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(trustRootSubjectPublicKeyInfo, out var bytesRead);
            var signature = Convert.FromBase64String(signedPolicy.SignatureBase64);
            if (bytesRead != trustRootSubjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                !verifier.VerifyData(CreateCanonicalPolicy(signedPolicy.Policy), signature, HashAlgorithmName.SHA256))
            {
                throw new GlobalReplicaException("The global trust-policy signature is invalid.");
            }
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new GlobalReplicaException($"The global trust-policy material is invalid: {exception.GetType().Name}.");
        }
    }

    public static IReadOnlyList<byte[]> AuthorizedReleaseKeys(GlobalTrustPolicy policy, long releaseSequence)
    {
        Validate(policy);
        if (releaseSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseSequence));
        }

        return policy.ReleaseKeys
            .Where(key => releaseSequence >= key.ValidFromSequence &&
                          (key.ValidThroughSequence is null || releaseSequence <= key.ValidThroughSequence) &&
                          (key.RevokedFromSequence is null || releaseSequence < key.RevokedFromSequence))
            .Select(key => Convert.FromBase64String(key.SubjectPublicKeyInfoBase64))
            .ToArray();
    }

    public static void Validate(GlobalTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SchemaVersion != CurrentSchemaVersion || policy.Revision < 1 || policy.ReleaseKeys is null ||
            policy.ReleaseKeys.Count is < 1 or > MaximumReleaseKeys)
        {
            throw new GlobalReplicaException("The global trust-policy schema, revision, or key count is invalid.");
        }

        if ((policy.Revision == 1 && policy.PreviousPolicySha256 is not null) ||
            (policy.Revision > 1 && !IsSha256(policy.PreviousPolicySha256)))
        {
            throw new GlobalReplicaException("The global trust-policy predecessor is invalid.");
        }

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in policy.ReleaseKeys)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.KeyId) ||
                string.IsNullOrWhiteSpace(key.SubjectPublicKeyInfoBase64) ||
                key.SubjectPublicKeyInfoBase64.Length > 2048)
            {
                throw new GlobalReplicaException("A release signing-key authorization is invalid.");
            }

            byte[] subjectPublicKeyInfo;
            try
            {
                subjectPublicKeyInfo = Convert.FromBase64String(key.SubjectPublicKeyInfoBase64);
            }
            catch (FormatException exception)
            {
                throw new GlobalReplicaException($"A release signing key is malformed: {exception.GetType().Name}.");
            }

            if (subjectPublicKeyInfo.Length is < 64 or > 1024 || !IsSha256(key.KeyId) ||
                !key.KeyId.Equals(GlobalReleaseCodec.Sha256(subjectPublicKeyInfo), StringComparison.OrdinalIgnoreCase) ||
                !identifiers.Add(key.KeyId) || key.ValidFromSequence < 1 ||
                key.ValidThroughSequence < key.ValidFromSequence ||
                key.RevokedFromSequence < key.ValidFromSequence)
            {
                throw new GlobalReplicaException("A release signing-key authorization is invalid.");
            }

            try
            {
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
                if (bytesRead != subjectPublicKeyInfo.Length || verifier.KeySize != 256)
                {
                    throw new GlobalReplicaException("A release signing key is not an exact P-256 public key.");
                }
            }
            catch (GlobalReplicaException)
            {
                throw;
            }
            catch (CryptographicException exception)
            {
                throw new GlobalReplicaException($"A release signing key is invalid: {exception.GetType().Name}.");
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private sealed record Envelope(string PolicyBase64, string SignatureBase64);
}
