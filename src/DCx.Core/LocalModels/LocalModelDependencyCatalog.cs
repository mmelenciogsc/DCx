using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public enum LocalModelDependencyKind
{
    WhisperRuntimeArchive,
    WhisperModel,
    OllamaManifest,
}

public sealed record LocalModelDependencyLicense(
    string Identifier,
    Uri SourceUri,
    long ExpectedBytes,
    string Sha256,
    bool RequiresContainedReview);

public sealed record OllamaManagedLayer(
    string MediaType,
    string Sha256,
    long ExpectedBytes);

public sealed record PinnedLocalModelDependencyCandidate(
    string Id,
    LocalModelDependencyKind Kind,
    string DisplayName,
    Uri SourceUri,
    long ExpectedBytes,
    string Sha256,
    LocalModelDependencyLicense License,
    IReadOnlyList<OllamaManagedLayer> ManagedLayers);

public sealed record LocalModelDependencyCatalogReview(
    int SchemaVersion,
    string CatalogId,
    string CatalogSha256,
    IReadOnlyList<PinnedLocalModelDependencyCandidate> Candidates);

public sealed class LocalModelDependencyCatalogLoader
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumCatalogBytes = 64 * 1024;
    public const string EmbeddedCatalogId = "dcx-p06-dependencies-2026-10-18";
    public const string EmbeddedCatalogSha256 = "1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325";
    private const string EmbeddedResourceName = "DCx.Core.LocalModels.p06-local-dependencies-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 24,
    };

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.Ordinal)
    {
        "github.com",
        "huggingface.co",
        "raw.githubusercontent.com",
        "registry.ollama.ai",
    };

    private static readonly HashSet<string> AllowedLayerTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.ollama.image.license",
        "application/vnd.ollama.image.model",
        "application/vnd.ollama.image.params",
    };

    public async Task<LocalModelDependencyCatalogReview> LoadEmbeddedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("The built-in local-model dependency catalog is unavailable.");
        var review = await LoadStreamAsync(stream, EmbeddedCatalogSha256, cancellationToken).ConfigureAwait(false);
        var requiredIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "ollama-nomic-embed-text-latest",
            "whisper-cuda-runtime-b5454",
            "whisper-model-base-multilingual",
            "whisper-model-large-v3-turbo-q5-0",
        };
        if (!string.Equals(review.CatalogId, EmbeddedCatalogId, StringComparison.Ordinal) ||
            review.Candidates.Count != requiredIds.Count ||
            !review.Candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal).SetEquals(requiredIds))
        {
            throw new InvalidOperationException("The built-in local-model dependency catalog identity is invalid.");
        }
        return review;
    }

    public async Task<LocalModelDependencyCatalogReview> LoadFileAsync(
        string path,
        string expectedCatalogSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateSha256(expectedCatalogSha256, "catalog");
        var canonical = LocalModelStoragePathGuard.ValidateFile(path, mustExist: true, "dependency catalog");
        if (!string.Equals(Path.GetExtension(canonical), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The dependency catalog must be a JSON file.");
        }

        var file = new FileInfo(canonical);
        if (file.Length is <= 0 or > MaximumCatalogBytes)
        {
            throw new InvalidOperationException("The dependency catalog is outside its size limit.");
        }

        await using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        LocalModelStoragePathGuard.ValidateFile(canonical, mustExist: true, "dependency catalog");
        if (stream.Length != file.Length)
        {
            throw new InvalidOperationException("The dependency catalog length changed during review.");
        }
        return await LoadStreamAsync(stream, expectedCatalogSha256, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<LocalModelDependencyCatalogReview> LoadStreamAsync(
        Stream stream,
        string expectedCatalogSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateSha256(expectedCatalogSha256, "catalog");
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("The dependency catalog stream is unreadable.");
        }

        var bytes = await ReadBoundedAsync(stream, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            RequireDigest(actualSha256, expectedCatalogSha256, "dependency catalog");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24,
            });
            RejectDuplicateProperties(document.RootElement);
            var dto = JsonSerializer.Deserialize<CatalogDto>(bytes, JsonOptions)
                ?? throw new InvalidOperationException("The dependency catalog is empty.");
            return ValidateCatalog(dto, actualSha256);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The dependency catalog is malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void ValidateCandidate(PinnedLocalModelDependencyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateIdentifier(candidate.Id, "candidate");
        ValidateDisplayName(candidate.DisplayName);
        ValidateUri(candidate.SourceUri, "candidate source");
        ValidateExpectedBytes(candidate.ExpectedBytes, 1024L * 1024 * 1024, "candidate");
        ValidateSha256(candidate.Sha256, "candidate");
        ArgumentNullException.ThrowIfNull(candidate.License);
        ValidateLicense(candidate.License, candidate.Kind);
        ArgumentNullException.ThrowIfNull(candidate.ManagedLayers);
        if (candidate.ManagedLayers.Count > 8)
        {
            throw new InvalidOperationException("The dependency candidate has too many managed layers.");
        }

        var expectedExtension = candidate.Kind switch
        {
            LocalModelDependencyKind.WhisperRuntimeArchive => ".zip",
            LocalModelDependencyKind.WhisperModel => ".bin",
            LocalModelDependencyKind.OllamaManifest => string.Empty,
            _ => throw new InvalidOperationException("The dependency candidate kind is unsupported."),
        };
        if (expectedExtension.Length > 0 &&
            !candidate.SourceUri.AbsolutePath.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The dependency candidate source type is inconsistent.");
        }
        if (candidate.Kind == LocalModelDependencyKind.OllamaManifest &&
            !candidate.SourceUri.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Ollama dependency source is not a manifest.");
        }

        if (candidate.Kind == LocalModelDependencyKind.WhisperRuntimeArchive &&
            !candidate.License.RequiresContainedReview)
        {
            throw new InvalidOperationException("The runtime archive must require contained-license review.");
        }
        if (candidate.Kind != LocalModelDependencyKind.WhisperRuntimeArchive &&
            candidate.License.RequiresContainedReview)
        {
            throw new InvalidOperationException("Only a runtime archive may require contained-license review.");
        }

        if (candidate.Kind != LocalModelDependencyKind.OllamaManifest)
        {
            if (candidate.ManagedLayers.Count != 0)
            {
                throw new InvalidOperationException("Only an Ollama manifest may declare managed layers.");
            }
            return;
        }

        if (candidate.ManagedLayers.Count != AllowedLayerTypes.Count)
        {
            throw new InvalidOperationException("The Ollama manifest must declare its exact managed layers.");
        }
        var mediaTypes = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in candidate.ManagedLayers)
        {
            if (layer is null || !AllowedLayerTypes.Contains(layer.MediaType) || !mediaTypes.Add(layer.MediaType))
            {
                throw new InvalidOperationException("The Ollama managed-layer type is invalid or duplicated.");
            }
            ValidateSha256(layer.Sha256, "managed layer");
            if (!digests.Add(layer.Sha256))
            {
                throw new InvalidOperationException("The Ollama managed-layer digest is duplicated.");
            }
            ValidateExpectedBytes(layer.ExpectedBytes, 1024L * 1024 * 1024, "managed layer");
        }
        var licenseLayer = candidate.ManagedLayers.Single(layer =>
            string.Equals(layer.MediaType, "application/vnd.ollama.image.license", StringComparison.Ordinal));
        if (!string.Equals(licenseLayer.Sha256, candidate.License.Sha256, StringComparison.Ordinal) ||
            licenseLayer.ExpectedBytes != candidate.License.ExpectedBytes)
        {
            throw new InvalidOperationException("The Ollama license layer does not match the reviewed license.");
        }
    }

    private static LocalModelDependencyCatalogReview ValidateCatalog(CatalogDto dto, string catalogSha256)
    {
        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("The dependency catalog schema is unsupported.");
        }
        ValidateIdentifier(dto.CatalogId, "catalog");
        if (dto.Candidates is null || dto.Candidates.Length is < 1 or > 16 || dto.Candidates.Any(item => item is null))
        {
            throw new InvalidOperationException("The dependency catalog candidate list is invalid.");
        }

        var candidates = new List<PinnedLocalModelDependencyCandidate>(dto.Candidates.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in dto.Candidates)
        {
            var candidate = CreateCandidate(item!);
            ValidateCandidate(candidate);
            if (!ids.Add(candidate.Id))
            {
                throw new InvalidOperationException("The dependency catalog contains a duplicate candidate identifier.");
            }
            candidates.Add(candidate);
        }
        return new LocalModelDependencyCatalogReview(
            dto.SchemaVersion,
            dto.CatalogId!,
            catalogSha256,
            candidates.AsReadOnly());
    }

    private static PinnedLocalModelDependencyCandidate CreateCandidate(CandidateDto dto)
    {
        if (!Enum.TryParse<LocalModelDependencyKind>(dto.Kind, ignoreCase: true, out var kind) ||
            !string.Equals(dto.Kind, KindName(kind), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The dependency candidate kind is unsupported.");
        }
        if (dto.License is null || dto.ManagedLayers is null || dto.ManagedLayers.Any(layer => layer is null))
        {
            throw new InvalidOperationException("The dependency candidate metadata is incomplete.");
        }
        var license = new LocalModelDependencyLicense(
            dto.License.Identifier!,
            ParseUri(dto.License.SourceUri, "license source"),
            dto.License.ExpectedBytes,
            dto.License.Sha256!,
            dto.License.RequiresContainedReview);
        var layers = dto.ManagedLayers.Select(layer => new OllamaManagedLayer(
            layer!.MediaType!,
            layer.Sha256!,
            layer.ExpectedBytes)).ToArray();
        return new PinnedLocalModelDependencyCandidate(
            dto.Id!,
            kind,
            dto.DisplayName!,
            ParseUri(dto.SourceUri, "candidate source"),
            dto.ExpectedBytes,
            dto.Sha256!,
            license,
            Array.AsReadOnly(layers));
    }

    private static void ValidateLicense(LocalModelDependencyLicense license, LocalModelDependencyKind kind)
    {
        if (string.IsNullOrWhiteSpace(license.Identifier) || license.Identifier.Length > 128 ||
            license.Identifier != license.Identifier.Trim() || license.Identifier.Any(char.IsControl))
        {
            throw new InvalidOperationException("The dependency license identifier is invalid.");
        }
        ValidateUri(license.SourceUri, "license source");
        ValidateExpectedBytes(license.ExpectedBytes, 128 * 1024, "license");
        ValidateSha256(license.Sha256, "license");
        if (kind == LocalModelDependencyKind.OllamaManifest &&
            !string.Equals(license.SourceUri.IdnHost, "registry.ollama.ai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Ollama license must come from the reviewed registry.");
        }
    }

    internal static void ValidateIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value != value.Trim() ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new InvalidOperationException($"The dependency {label} identifier is invalid.");
        }
    }

    private static void ValidateDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new InvalidOperationException("The dependency candidate display name is invalid.");
        }
    }

    private static Uri ParseUri(string? value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"The dependency {label} URI is invalid.");
        }
        return uri;
    }

    internal static void ValidateUri(Uri uri, string label)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !AllowedHosts.Contains(uri.IdnHost) ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal) || uri.AbsolutePath.Length > 512)
        {
            throw new InvalidOperationException($"The dependency {label} URI is not an approved HTTPS source.");
        }
    }

    private static void ValidateExpectedBytes(long value, long maximum, string label)
    {
        if (value <= 0 || value > maximum)
        {
            throw new InvalidOperationException($"The dependency {label} size is invalid.");
        }
    }

    internal static void ValidateSha256(string? value, string label)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException($"The dependency {label} SHA-256 is invalid.");
        }
    }

    internal static void RequireDigest(string actual, string expected, string label)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected)))
        {
            throw new InvalidOperationException($"The {label} SHA-256 does not match the pinned value.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > maximumBytes)
                {
                    throw new InvalidOperationException("The dependency catalog exceeds its size limit.");
                }
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0)
            {
                throw new InvalidOperationException("The dependency catalog is empty.");
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
        }
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
                    throw new InvalidOperationException("The dependency catalog contains a duplicate property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static string KindName(LocalModelDependencyKind kind) => kind switch
    {
        LocalModelDependencyKind.WhisperRuntimeArchive => "whisperRuntimeArchive",
        LocalModelDependencyKind.WhisperModel => "whisperModel",
        LocalModelDependencyKind.OllamaManifest => "ollamaManifest",
        _ => throw new InvalidOperationException("The dependency candidate kind is unsupported."),
    };

    private sealed class CatalogDto
    {
        public int SchemaVersion { get; init; }
        public string? CatalogId { get; init; }
        public CandidateDto?[]? Candidates { get; init; }
    }

    private sealed class CandidateDto
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? DisplayName { get; init; }
        public string? SourceUri { get; init; }
        public long ExpectedBytes { get; init; }
        public string? Sha256 { get; init; }
        public LicenseDto? License { get; init; }
        public ManagedLayerDto?[]? ManagedLayers { get; init; }
    }

    private sealed class LicenseDto
    {
        public string? Identifier { get; init; }
        public string? SourceUri { get; init; }
        public long ExpectedBytes { get; init; }
        public string? Sha256 { get; init; }
        public bool RequiresContainedReview { get; init; }
    }

    private sealed class ManagedLayerDto
    {
        public string? MediaType { get; init; }
        public string? Sha256 { get; init; }
        public long ExpectedBytes { get; init; }
    }
}

public sealed record LocalModelDependencyArtifactReview(
    string CandidateId,
    LocalModelDependencyKind Kind,
    string ArtifactPath,
    long Bytes,
    string Sha256,
    DateTimeOffset ReviewedUtc,
    WhisperRuntimeArchiveInventory? RuntimeArchive);

public sealed class LocalModelDependencyArtifactReviewer
{
    private readonly TimeProvider timeProvider;

    public LocalModelDependencyArtifactReviewer(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LocalModelDependencyArtifactReview> ReviewAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
        var canonical = LocalModelStoragePathGuard.ValidateFile(
            artifactPath,
            mustExist: true,
            "dependency artifact");
        ValidateArtifactExtension(candidate.Kind, canonical);
        var file = new FileInfo(canonical);
        if (file.Length != candidate.ExpectedBytes)
        {
            throw new InvalidOperationException("The dependency artifact length does not match the pinned value.");
        }

        await using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        LocalModelStoragePathGuard.ValidateFile(canonical, mustExist: true, "dependency artifact");
        if (stream.Length != candidate.ExpectedBytes)
        {
            throw new InvalidOperationException("The dependency artifact length changed during review.");
        }
        return await ReviewLeasedAsync(candidate, canonical, stream, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<LocalModelDependencyArtifactReview> ReviewLeasedAsync(
        PinnedLocalModelDependencyCandidate candidate,
        string artifactPath,
        FileStream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
        var canonical = LocalModelStoragePathGuard.ValidateFile(
            artifactPath,
            mustExist: true,
            "dependency artifact");
        ValidateArtifactExtension(candidate.Kind, canonical);
        if (!stream.CanRead || !stream.CanSeek || stream.Length != candidate.ExpectedBytes)
        {
            throw new InvalidOperationException("The dependency artifact lease is inconsistent with the pinned value.");
        }
        stream.Position = 0;
        await ValidateSignatureAsync(candidate, stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (stream.Length != candidate.ExpectedBytes)
        {
            throw new InvalidOperationException("The dependency artifact length changed during review.");
        }
        LocalModelDependencyCatalogLoader.RequireDigest(actual, candidate.Sha256, "dependency artifact");

        WhisperRuntimeArchiveInventory? runtimeArchive = null;
        if (candidate.Kind == LocalModelDependencyKind.WhisperRuntimeArchive)
        {
            stream.Position = 0;
            runtimeArchive = await WhisperRuntimeArchiveInspector.InspectAsync(stream, cancellationToken)
                .ConfigureAwait(false);
        }

        var reviewedUtc = timeProvider.GetUtcNow();
        if (reviewedUtc == default || reviewedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The dependency review clock did not return UTC.");
        }
        return new LocalModelDependencyArtifactReview(
            candidate.Id,
            candidate.Kind,
            canonical,
            candidate.ExpectedBytes,
            actual,
            reviewedUtc,
            runtimeArchive);
    }

    private static async Task ValidateSignatureAsync(
        PinnedLocalModelDependencyCandidate candidate,
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        try
        {
            if (await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false) != prefix.Length)
            {
                throw new InvalidOperationException("The dependency artifact signature is incomplete.");
            }
            if (candidate.Kind == LocalModelDependencyKind.WhisperRuntimeArchive &&
                !prefix.AsSpan().SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 }))
            {
                throw new InvalidOperationException("The Whisper runtime artifact is not a ZIP archive.");
            }
            if (candidate.Kind == LocalModelDependencyKind.WhisperModel &&
                !prefix.AsSpan().SequenceEqual("ggml"u8))
            {
                throw new InvalidOperationException("The Whisper model artifact has an invalid signature.");
            }
            if (candidate.Kind == LocalModelDependencyKind.OllamaManifest)
            {
                stream.Position = 0;
                var bytes = new byte[checked((int)stream.Length)];
                try
                {
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    ValidateOllamaManifest(candidate, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
        }
    }

    private static void ValidateOllamaManifest(
        PinnedLocalModelDependencyCandidate candidate,
        ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 2 ||
                !root.TryGetProperty("mediaType", out var media) || media.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    media.GetString(),
                    "application/vnd.docker.distribution.manifest.v2+json",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array ||
                layers.GetArrayLength() != candidate.ManagedLayers.Count)
            {
                throw new InvalidOperationException("The Ollama dependency manifest shape is invalid.");
            }
            RequireOciDescriptor(config, "application/vnd.docker.container.image.v1+json");
            var expected = candidate.ManagedLayers.ToDictionary(layer => layer.MediaType, StringComparer.Ordinal);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object ||
                    !layer.TryGetProperty("mediaType", out var layerMedia) || layerMedia.ValueKind != JsonValueKind.String ||
                    layerMedia.GetString() is not { } mediaType || !expected.TryGetValue(mediaType, out var expectedLayer) ||
                    !observed.Add(mediaType))
                {
                    throw new InvalidOperationException("The Ollama dependency manifest layer is invalid.");
                }
                RequireOciDescriptor(layer, mediaType, expectedLayer.Sha256, expectedLayer.ExpectedBytes);
            }
            if (observed.Count != expected.Count)
            {
                throw new InvalidOperationException("The Ollama dependency manifest layer inventory is incomplete.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Ollama dependency manifest is malformed.", exception);
        }
    }

    private static void RequireOciDescriptor(
        JsonElement descriptor,
        string expectedMediaType,
        string? expectedSha256 = null,
        long? expectedBytes = null)
    {
        if (!descriptor.TryGetProperty("mediaType", out var media) || media.ValueKind != JsonValueKind.String ||
            !string.Equals(media.GetString(), expectedMediaType, StringComparison.Ordinal) ||
            !descriptor.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            digest.GetString() is not { } digestValue || !digestValue.StartsWith("sha256:", StringComparison.Ordinal) ||
            !descriptor.TryGetProperty("size", out var size) || !size.TryGetInt64(out var byteCount) || byteCount <= 0)
        {
            throw new InvalidOperationException("The Ollama dependency manifest descriptor is invalid.");
        }
        LocalModelDependencyCatalogLoader.ValidateSha256(digestValue[7..], "manifest descriptor");
        if (expectedSha256 is not null &&
            (expectedBytes is null ||
             !string.Equals(digestValue[7..], expectedSha256, StringComparison.Ordinal) ||
             byteCount != expectedBytes.Value))
        {
            throw new InvalidOperationException("The Ollama dependency manifest descriptor changed.");
        }
    }

    private static void ValidateArtifactExtension(LocalModelDependencyKind kind, string path)
    {
        var extension = Path.GetExtension(path);
        var valid = kind switch
        {
            LocalModelDependencyKind.WhisperRuntimeArchive =>
                string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase),
            LocalModelDependencyKind.WhisperModel =>
                string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase),
            LocalModelDependencyKind.OllamaManifest =>
                string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidOperationException("The dependency artifact file type is inconsistent.");
        }
    }
}
