using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.GlobalKnowledge;

public static class GlobalReleaseCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumChangesPerRelease = 10_000;
    public const int MaximumEmbeddingDimensions = 4096;
    public const int MaximumReleaseBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static byte[] CreateCanonicalManifest(GlobalReleaseManifest manifest)
    {
        Validate(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteNumber("sequence", manifest.Sequence);
            writer.WriteNumber("previousSequence", manifest.PreviousSequence);
            if (manifest.PreviousReleaseSha256 is null)
            {
                writer.WriteNull("previousReleaseSha256");
            }
            else
            {
                writer.WriteString("previousReleaseSha256", manifest.PreviousReleaseSha256.ToLowerInvariant());
            }

            writer.WriteString("resultCatalogSha256", manifest.ResultCatalogSha256.ToLowerInvariant());
            writer.WriteString("issuedUtc", manifest.IssuedUtc.ToUniversalTime().ToString("O"));
            writer.WriteStartArray("changes");
            foreach (var change in manifest.Changes
                         .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
                         .ThenBy(item => item.Kind)
                         .ThenBy(item => item.Version, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("kind", (int)change.Kind);
                writer.WriteString("resourceId", change.ResourceId);
                writer.WriteString("version", change.Version);
                if (change.Resource is null)
                {
                    writer.WriteNull("resource");
                }
                else
                {
                    writer.WritePropertyName("resource");
                    WriteResource(writer, change.Resource);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static byte[] Encode(SignedGlobalRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var payload = CreateCanonicalManifest(release.Manifest);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(release.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new GlobalReplicaException($"The release signature is malformed: {exception.GetType().Name}.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature)), JsonOptions);
    }

    public static SignedGlobalRelease Decode(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumReleaseBytes)
        {
            throw new GlobalReplicaException("The release envelope is empty or too large.");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes, JsonOptions)
                ?? throw new GlobalReplicaException("The release envelope is empty.");
            var payload = Convert.FromBase64String(envelope.ManifestBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            if (payload.Length == 0 || signature.Length == 0)
            {
                throw new GlobalReplicaException("The release envelope has an empty payload or signature.");
            }

            var manifest = JsonSerializer.Deserialize<GlobalReleaseManifest>(payload, JsonOptions)
                ?? throw new GlobalReplicaException("The release manifest is empty.");
            Validate(manifest);
            if (!payload.AsSpan().SequenceEqual(CreateCanonicalManifest(manifest)))
            {
                throw new GlobalReplicaException("The release manifest is not in canonical form.");
            }

            return new SignedGlobalRelease(manifest, Convert.ToBase64String(signature));
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new GlobalReplicaException($"The release envelope is malformed: {exception.GetType().Name}.");
        }
    }

    public static SignedGlobalRelease Sign(GlobalReleaseManifest manifest, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        var signature = signingKey.SignData(CreateCanonicalManifest(manifest), HashAlgorithmName.SHA256);
        return new SignedGlobalRelease(manifest, Convert.ToBase64String(signature));
    }

    public static void Verify(SignedGlobalRelease release, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(release);
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            var signature = Convert.FromBase64String(release.SignatureBase64);
            if (bytesRead != subjectPublicKeyInfo.Length ||
                !verifier.VerifyData(CreateCanonicalManifest(release.Manifest), signature, HashAlgorithmName.SHA256))
            {
                throw new GlobalReplicaException("The global release signature is invalid.");
            }
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new GlobalReplicaException($"The global release trust material is invalid: {exception.GetType().Name}.");
        }
    }

    public static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static byte[] CreateCanonicalCatalog(IEnumerable<GlobalResourceRecord> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteStartArray("resources");
            foreach (var resource in resources.OrderBy(item => item.ResourceId, StringComparer.Ordinal))
            {
                ValidateResource(resource);
                WriteResource(writer, resource);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<GlobalResourceRecord> DecodeCatalog(ReadOnlySpan<byte> catalogBytes)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<Catalog>(catalogBytes, JsonOptions)
                ?? throw new GlobalReplicaException("The global catalog is empty.");
            if (catalog.SchemaVersion != CurrentSchemaVersion || catalog.Resources.Count > 100_000 ||
                catalog.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != catalog.Resources.Count)
            {
                throw new GlobalReplicaException("The global catalog schema, size, or identifiers are invalid.");
            }

            if (!catalogBytes.SequenceEqual(CreateCanonicalCatalog(catalog.Resources)))
            {
                throw new GlobalReplicaException("The global catalog is not in canonical form.");
            }

            return catalog.Resources;
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new GlobalReplicaException($"The global catalog is malformed: {exception.GetType().Name}.");
        }
    }

    public static void Validate(GlobalReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != CurrentSchemaVersion || manifest.Sequence < 1 ||
            manifest.PreviousSequence != manifest.Sequence - 1 ||
            manifest.Changes.Count is 0 or > MaximumChangesPerRelease ||
            !IsSha256(manifest.ResultCatalogSha256))
        {
            throw new GlobalReplicaException("The global release schema, sequence, or change count is invalid.");
        }

        if (manifest.PreviousSequence == 0)
        {
            if (manifest.PreviousReleaseSha256 is not null)
            {
                throw new GlobalReplicaException("The first release cannot name a previous digest.");
            }
        }
        else if (!IsSha256(manifest.PreviousReleaseSha256))
        {
            throw new GlobalReplicaException("A non-initial release requires a valid previous digest.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in manifest.Changes)
        {
            if (!Enum.IsDefined(change.Kind))
            {
                throw new GlobalReplicaException("A global release change kind is invalid.");
            }

            ValidateIdentifier(change.ResourceId, 128);
            ValidateIdentifier(change.Version, 64);
            if (!identifiers.Add(change.ResourceId))
            {
                throw new GlobalReplicaException("A release cannot change the same resource more than once.");
            }

            if (change.Kind == GlobalReleaseChangeKind.Upsert)
            {
                if (change.Resource is null || change.Resource.ResourceId != change.ResourceId ||
                    change.Resource.Version != change.Version)
                {
                    throw new GlobalReplicaException("An upsert must contain matching resource metadata.");
                }

                ValidateResource(change.Resource);
            }
            else if (change.Resource is not null)
            {
                throw new GlobalReplicaException("A deprecation or removal cannot embed replacement metadata.");
            }
        }
    }

    private static void ValidateResource(GlobalResourceRecord resource)
    {
        if (!Enum.IsDefined(resource.Kind))
        {
            throw new GlobalReplicaException("The global resource kind is invalid.");
        }

        ValidateText(resource.Title, 512);
        ValidateText(resource.Summary, 64 * 1024);
        ValidateText(resource.SourceUri, 2048);
        ValidateText(resource.License, 256);
        if (resource.Capabilities.Count > 64 ||
            resource.Capabilities.Any(value => !IsIdentifier(value, 128)) ||
            resource.Embedding is { Length: 0 } || resource.Embedding is { Length: > MaximumEmbeddingDimensions } ||
            resource.Embedding?.Any(value => !float.IsFinite(value)) == true)
        {
            throw new GlobalReplicaException("The global resource capabilities or embedding are invalid.");
        }

        if (resource.Artifact is not null &&
            (!IsSha256(resource.Artifact.Sha256) || resource.Artifact.SizeBytes is <= 0 or > ContentAddressedArtifactLimit ||
             string.IsNullOrWhiteSpace(resource.Artifact.MediaType) || resource.Artifact.MediaType.Length > 256))
        {
            throw new GlobalReplicaException("The global artifact reference is invalid.");
        }
    }

    private static void WriteResource(Utf8JsonWriter writer, GlobalResourceRecord resource)
    {
        writer.WriteStartObject();
        writer.WriteString("resourceId", resource.ResourceId);
        writer.WriteString("version", resource.Version);
        writer.WriteNumber("kind", (int)resource.Kind);
        writer.WriteString("title", resource.Title);
        writer.WriteString("summary", resource.Summary);
        writer.WriteString("sourceUri", resource.SourceUri);
        writer.WriteString("license", resource.License);
        writer.WriteStartArray("capabilities");
        foreach (var capability in resource.Capabilities.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(capability);
        }

        writer.WriteEndArray();
        if (resource.Embedding is null)
        {
            writer.WriteNull("embedding");
        }
        else
        {
            writer.WriteStartArray("embedding");
            foreach (var value in resource.Embedding)
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
        }

        if (resource.Artifact is null)
        {
            writer.WriteNull("artifact");
        }
        else
        {
            writer.WriteStartObject("artifact");
            writer.WriteString("sha256", resource.Artifact.Sha256.ToLowerInvariant());
            writer.WriteNumber("sizeBytes", resource.Artifact.SizeBytes);
            writer.WriteString("mediaType", resource.Artifact.MediaType);
            writer.WriteEndObject();
        }

        writer.WriteBoolean("deprecated", resource.Deprecated);
        writer.WriteEndObject();
    }

    private const long ContentAddressedArtifactLimit = 256L * 1024 * 1024;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/');

    private static void ValidateIdentifier(string value, int maximumLength)
    {
        if (!IsIdentifier(value, maximumLength))
        {
            throw new GlobalReplicaException("A global resource identifier is invalid.");
        }
    }

    private static void ValidateText(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Contains('\0'))
        {
            throw new GlobalReplicaException("Global resource text is empty, too long, or contains a null character.");
        }
    }

    private sealed record Envelope(string ManifestBase64, string SignatureBase64);

    private sealed record Catalog(int SchemaVersion, IReadOnlyList<GlobalResourceRecord> Resources);
}
