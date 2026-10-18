using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelDependencyCatalogTests : IDisposable
{
    private static readonly DateTimeOffset ReviewedUtc =
        new(2026, 10, 18, 6, 30, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-dependency-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task EmbeddedCatalog_BindsExactReviewedCandidatesLicensesAndLayers()
    {
        var review = await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync();

        Assert.Equal(1, review.SchemaVersion);
        Assert.Equal(LocalModelDependencyCatalogLoader.EmbeddedCatalogId, review.CatalogId);
        Assert.Equal(LocalModelDependencyCatalogLoader.EmbeddedCatalogSha256, review.CatalogSha256);
        Assert.Equal(4, review.Candidates.Count);
        var runtime = Assert.Single(review.Candidates, candidate =>
            candidate.Id == "whisper-cuda-runtime-b5454");
        Assert.Equal(LocalModelDependencyKind.WhisperRuntimeArchive, runtime.Kind);
        Assert.Equal(284_761_231, runtime.ExpectedBytes);
        Assert.True(runtime.License.RequiresContainedReview);
        var embedding = Assert.Single(review.Candidates, candidate =>
            candidate.Id == "ollama-nomic-embed-text-latest");
        Assert.Equal(LocalModelDependencyKind.OllamaManifest, embedding.Kind);
        Assert.Equal(3, embedding.ManagedLayers.Count);
        Assert.Contains(embedding.ManagedLayers, layer =>
            layer.MediaType == "application/vnd.ollama.image.model" &&
            layer.ExpectedBytes == 274_290_656 &&
            layer.Sha256 == "970aa74c0a90ef7482477cf803618e776e173c007bf957f635f1015bfcfef0e6");
        Assert.Contains(embedding.ManagedLayers, layer =>
            layer.MediaType == "application/vnd.ollama.image.license" &&
            layer.Sha256 == embedding.License.Sha256);
    }

    [Fact]
    public async Task Catalog_BindsOnlyWhenExactDigestAndSchemaMatch()
    {
        var bytes = Encoding.UTF8.GetBytes(ValidCatalogJson());
        var digest = Digest(bytes);

        var review = await new LocalModelDependencyCatalogLoader().LoadStreamAsync(
            new MemoryStream(bytes),
            digest);

        Assert.Equal("test-catalog", review.CatalogId);
        var candidate = Assert.Single(review.Candidates);
        Assert.Equal("test-whisper-model", candidate.Id);
        Assert.Equal(LocalModelDependencyKind.WhisperModel, candidate.Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyCatalogLoader().LoadStreamAsync(
                new MemoryStream(bytes),
                new string('0', 64)));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("unapproved-host")]
    [InlineData("insecure-uri")]
    public async Task Catalog_DeniesAmbiguousUnknownOrUnapprovedInput(string mutation)
    {
        var json = mutation switch
        {
            "duplicate" => ValidCatalogJson().Replace(
                "\"catalogId\": \"test-catalog\",",
                "\"catalogId\": \"test-catalog\", \"catalogId\": \"replacement\",",
                StringComparison.Ordinal),
            "unknown" => ValidCatalogJson().Replace(
                "\"catalogId\": \"test-catalog\",",
                "\"catalogId\": \"test-catalog\", \"unexpected\": true,",
                StringComparison.Ordinal),
            "unapproved-host" => ValidCatalogJson().Replace(
                "https://huggingface.co/",
                "https://example.com/",
                StringComparison.Ordinal),
            "insecure-uri" => ValidCatalogJson().Replace(
                "https://huggingface.co/",
                "http://huggingface.co/",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown catalog mutation."),
        };
        var bytes = Encoding.UTF8.GetBytes(json);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyCatalogLoader().LoadStreamAsync(
                new MemoryStream(bytes),
                Digest(bytes)));
    }

    [Fact]
    public async Task CatalogFile_DeniesLinkedFileWithoutReadingOutsideContent()
    {
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside.json");
        var bytes = Encoding.UTF8.GetBytes(ValidCatalogJson());
        await File.WriteAllBytesAsync(outside, bytes);
        var linked = Path.Combine(root, "catalog.json");
        File.CreateSymbolicLink(linked, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyCatalogLoader().LoadFileAsync(linked, Digest(bytes)));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task ArtifactReview_LeasesAndAuthenticatesRuntimeArchive()
    {
        Directory.CreateDirectory(root);
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4 };
        var path = Path.Combine(root, "runtime.zip");
        await File.WriteAllBytesAsync(path, bytes);
        var candidate = RuntimeCandidate(bytes);

        var review = await new LocalModelDependencyArtifactReviewer(
            new FixedTimeProvider(ReviewedUtc)).ReviewAsync(candidate, path);

        Assert.Equal(candidate.Id, review.CandidateId);
        Assert.Equal(path, review.ArtifactPath);
        Assert.Equal(bytes.Length, review.Bytes);
        Assert.Equal(Digest(bytes), review.Sha256);
        Assert.Equal(ReviewedUtc, review.ReviewedUtc);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("digest")]
    [InlineData("signature")]
    [InlineData("extension")]
    public async Task ArtifactReview_DeniesLengthDigestSignatureOrTypeMismatch(string mutation)
    {
        Directory.CreateDirectory(root);
        var valid = new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4 };
        var artifact = mutation == "signature"
            ? new byte[] { 0, 1, 2, 3, 1, 2, 3, 4 }
            : valid;
        var path = Path.Combine(root, mutation == "extension" ? "runtime.bin" : "runtime.zip");
        await File.WriteAllBytesAsync(path, artifact);
        var candidate = mutation switch
        {
            "length" => RuntimeCandidate(valid) with { ExpectedBytes = valid.Length + 1 },
            "digest" => RuntimeCandidate(valid) with { Sha256 = new string('0', 64) },
            "signature" => RuntimeCandidate(artifact),
            _ => RuntimeCandidate(valid),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyArtifactReviewer().ReviewAsync(candidate, path));
    }

    [Fact]
    public async Task ArtifactReview_BindsExactOllamaManagedLayerInventory()
    {
        Directory.CreateDirectory(root);
        var layers = ManagedLayers();
        var bytes = OllamaManifestBytes(layers);
        var path = Path.Combine(root, "manifest.json");
        await File.WriteAllBytesAsync(path, bytes);
        var candidate = OllamaCandidate(bytes, layers);

        var review = await new LocalModelDependencyArtifactReviewer(
            new FixedTimeProvider(ReviewedUtc)).ReviewAsync(candidate, path);

        Assert.Equal(LocalModelDependencyKind.OllamaManifest, review.Kind);
        Assert.Equal(Digest(bytes), review.Sha256);
    }

    [Fact]
    public async Task ArtifactReview_DeniesOllamaLayerSubstitutionEvenWithMatchingManifestDigest()
    {
        Directory.CreateDirectory(root);
        var expected = ManagedLayers();
        var substituted = expected.Select(layer =>
            layer.MediaType == "application/vnd.ollama.image.model"
                ? layer with { Sha256 = new string('9', 64) }
                : layer).ToArray();
        var bytes = OllamaManifestBytes(substituted);
        var path = Path.Combine(root, "manifest.json");
        await File.WriteAllBytesAsync(path, bytes);
        var candidate = OllamaCandidate(bytes, expected);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyArtifactReviewer().ReviewAsync(candidate, path));
    }

    [Fact]
    public async Task ArtifactReview_DeniesLinkedArtifactWithoutChangingOutsideFile()
    {
        Directory.CreateDirectory(root);
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4 };
        var outside = Path.Combine(root, "outside.zip");
        await File.WriteAllBytesAsync(outside, bytes);
        var linked = Path.Combine(root, "runtime.zip");
        File.CreateSymbolicLink(linked, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyArtifactReviewer().ReviewAsync(RuntimeCandidate(bytes), linked));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task ArtifactReview_DeniesLinkedAncestorWithoutChangingOutsideFile()
    {
        Directory.CreateDirectory(root);
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4 };
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "runtime.zip");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        var linked = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linked, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalModelDependencyArtifactReviewer().ReviewAsync(
                RuntimeCandidate(bytes),
                Path.Combine(linked, "runtime.zip")));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static PinnedLocalModelDependencyCandidate RuntimeCandidate(byte[] bytes) => new(
        "test-runtime",
        LocalModelDependencyKind.WhisperRuntimeArchive,
        "Test Whisper runtime",
        new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/test/runtime.zip"),
        bytes.Length,
        Digest(bytes),
        new LocalModelDependencyLicense(
            "MIT plus contained notices",
            new Uri("https://raw.githubusercontent.com/ggml-org/whisper.cpp/test/LICENSE"),
            100,
            new string('a', 64),
            true),
        []);

    private static PinnedLocalModelDependencyCandidate OllamaCandidate(
        byte[] bytes,
        IReadOnlyList<OllamaManagedLayer> layers)
    {
        var license = layers.Single(layer =>
            layer.MediaType == "application/vnd.ollama.image.license");
        return new PinnedLocalModelDependencyCandidate(
            "test-ollama-manifest",
            LocalModelDependencyKind.OllamaManifest,
            "Test Ollama manifest",
            new Uri("https://registry.ollama.ai/v2/library/test/manifests/latest"),
            bytes.Length,
            Digest(bytes),
            new LocalModelDependencyLicense(
                "Apache-2.0",
                new Uri($"https://registry.ollama.ai/v2/library/test/blobs/sha256:{license.Sha256}"),
                license.ExpectedBytes,
                license.Sha256,
                false),
            layers);
    }

    private static OllamaManagedLayer[] ManagedLayers() =>
    [
        new("application/vnd.ollama.image.model", new string('1', 64), 200),
        new("application/vnd.ollama.image.license", new string('2', 64), 100),
        new("application/vnd.ollama.image.params", new string('3', 64), 20),
    ];

    private static byte[] OllamaManifestBytes(IReadOnlyList<OllamaManagedLayer> layers) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.docker.distribution.manifest.v2+json",
            config = new
            {
                mediaType = "application/vnd.docker.container.image.v1+json",
                digest = "sha256:" + new string('f', 64),
                size = 50,
            },
            layers = layers.Select(layer => new
            {
                mediaType = layer.MediaType,
                digest = "sha256:" + layer.Sha256,
                size = layer.ExpectedBytes,
            }),
        });

    private static string ValidCatalogJson() => """
        {
          "schemaVersion": 1,
          "catalogId": "test-catalog",
          "candidates": [
            {
              "id": "test-whisper-model",
              "kind": "whisperModel",
              "displayName": "Test Whisper model",
              "sourceUri": "https://huggingface.co/test/resolve/main/model.bin",
              "expectedBytes": 8,
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "license": {
                "identifier": "MIT",
                "sourceUri": "https://raw.githubusercontent.com/openai/whisper/v20250625/LICENSE",
                "expectedBytes": 1063,
                "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "requiresContainedReview": false
              },
              "managedLayers": []
            }
          ]
        }
        """;

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
