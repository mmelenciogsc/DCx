using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Reliability;

public sealed class ControlledUpdateException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ControlledUpdateManifest(
    int SchemaVersion,
    string ProductId,
    string Version,
    DateTimeOffset PublishedUtc,
    DateTimeOffset ExpiresUtc,
    string PackageUri,
    long PackageLength,
    string PackageSha256,
    string PackageIdentityName,
    string PublisherId);

public sealed record SignedControlledUpdate(
    ControlledUpdateManifest Manifest,
    string SignatureBase64);

public static class ControlledUpdateManifestCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEnvelopeBytes = 64 * 1024;
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    public const string ProductId = "DCx.AgenticBrowser";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static byte[] CreateCanonicalManifest(ControlledUpdateManifest manifest)
    {
        Validate(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("productId", manifest.ProductId);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("publishedUtc", manifest.PublishedUtc.ToString("O"));
            writer.WriteString("expiresUtc", manifest.ExpiresUtc.ToString("O"));
            writer.WriteString("packageUri", manifest.PackageUri);
            writer.WriteNumber("packageLength", manifest.PackageLength);
            writer.WriteString("packageSha256", manifest.PackageSha256);
            writer.WriteString("packageIdentityName", manifest.PackageIdentityName);
            writer.WriteString("publisherId", manifest.PublisherId);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static SignedControlledUpdate Sign(ControlledUpdateManifest manifest, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.KeySize != 256)
        {
            throw new ControlledUpdateException("update_signing_key_invalid");
        }

        var signature = signingKey.SignData(
            CreateCanonicalManifest(manifest),
            HashAlgorithmName.SHA256);
        return new SignedControlledUpdate(manifest, Convert.ToBase64String(signature));
    }

    public static byte[] Encode(SignedControlledUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var manifest = CreateCanonicalManifest(update.Manifest);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(update.SignatureBase64);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new ControlledUpdateException("update_signature_invalid");
        }

        if (signature.Length == 0)
        {
            throw new ControlledUpdateException("update_signature_invalid");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("manifestBase64", Convert.ToBase64String(manifest));
            writer.WriteString("signatureBase64", Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        if (stream.Length > MaximumEnvelopeBytes)
        {
            throw new ControlledUpdateException("update_envelope_invalid");
        }

        return stream.ToArray();
    }

    public static SignedControlledUpdate Decode(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumEnvelopeBytes)
        {
            throw new ControlledUpdateException("update_envelope_invalid");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes, JsonOptions)
                ?? throw new ControlledUpdateException("update_envelope_invalid");
            if (string.IsNullOrEmpty(envelope.ManifestBase64) ||
                string.IsNullOrEmpty(envelope.SignatureBase64))
            {
                throw new ControlledUpdateException("update_envelope_invalid");
            }

            var manifestBytes = Convert.FromBase64String(envelope.ManifestBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            if (manifestBytes.Length == 0 || signature.Length == 0)
            {
                throw new ControlledUpdateException("update_envelope_invalid");
            }

            var manifest = JsonSerializer.Deserialize<ControlledUpdateManifest>(manifestBytes, JsonOptions)
                ?? throw new ControlledUpdateException("update_manifest_invalid");
            Validate(manifest);
            var signed = new SignedControlledUpdate(manifest, Convert.ToBase64String(signature));
            if (!manifestBytes.AsSpan().SequenceEqual(CreateCanonicalManifest(manifest)) ||
                !envelopeBytes.SequenceEqual(Encode(signed)))
            {
                throw new ControlledUpdateException("update_envelope_noncanonical");
            }

            return signed;
        }
        catch (ControlledUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException or ArgumentNullException)
        {
            throw new ControlledUpdateException("update_envelope_invalid");
        }
    }

    public static void Verify(
        SignedControlledUpdate update,
        ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            var signature = Convert.FromBase64String(update.SignatureBase64);
            if (bytesRead != subjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                !verifier.VerifyData(
                    CreateCanonicalManifest(update.Manifest),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                throw new ControlledUpdateException("update_signature_invalid");
            }
        }
        catch (ControlledUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or ArgumentNullException)
        {
            throw new ControlledUpdateException("update_trust_material_invalid");
        }
    }

    public static Version ParsePackageVersion(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 23 ||
            !Version.TryParse(value, out var version) ||
            version.Major is < 0 or > 65_535 ||
            version.Minor is < 0 or > 65_535 ||
            version.Build is < 0 or > 65_535 ||
            version.Revision is < 0 or > 65_535 ||
            !string.Equals(version.ToString(4), value, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_version_invalid");
        }

        return version;
    }

    public static Uri ParsePackageUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlledUpdateException("update_package_uri_invalid");
        }

        return uri;
    }

    public static void Validate(ControlledUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _ = ParsePackageVersion(manifest.Version);
        _ = ParsePackageUri(manifest.PackageUri);
        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(manifest.ProductId, ProductId, StringComparison.Ordinal) ||
            manifest.PublishedUtc.Offset != TimeSpan.Zero ||
            manifest.ExpiresUtc.Offset != TimeSpan.Zero ||
            manifest.ExpiresUtc <= manifest.PublishedUtc ||
            manifest.ExpiresUtc - manifest.PublishedUtc > TimeSpan.FromDays(30) ||
            manifest.PackageLength is < 1 or > MaximumPackageBytes ||
            !IsLowerSha256(manifest.PackageSha256) ||
            !IsIdentifier(manifest.PackageIdentityName, 128) ||
            !IsPublisher(manifest.PublisherId))
        {
            throw new ControlledUpdateException("update_manifest_invalid");
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsPublisher(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        value == value.Trim() && !value.Any(char.IsControl);

    private sealed record Envelope(string ManifestBase64, string SignatureBase64);
}
