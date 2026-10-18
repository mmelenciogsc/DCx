using System.Text.Json;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelBenchmarkManifestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-benchmark-manifest-{Guid.NewGuid():N}");

    [Fact]
    public async Task TranscriptionManifest_BindsLocalWaveSuitePolicyAndProvenance()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "speech.wav"), WaveBytes());
        var manifestPath = Path.Combine(root, "transcription.json");
        await File.WriteAllTextAsync(manifestPath, TranscriptionManifest());

        var review = Assert.IsType<TranscriptionBenchmarkManifestReview>(
            await LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));

        Assert.Equal(LocalModelBenchmarkKind.Transcription, review.Kind);
        Assert.Equal("DCx local speech fixture", review.Name);
        Assert.Equal("CC0-1.0", review.Provenance.License);
        Assert.Equal(64, review.ManifestSha256.Length);
        Assert.Equal(64, review.FixtureSuiteSha256.Length);
        Assert.Equal(64, review.PolicySha256.Length);
        Assert.Single(review.Fixtures);
        Assert.Equal(Path.Combine(root, "speech.wav"), review.Fixtures[0].WavePath);
        Assert.Equal(0.1, review.Limits.MaximumWordErrorRate);
        Assert.True(review.Limits.Resources.RequireUnloadConfirmation);
    }

    [Fact]
    public async Task EmbeddingManifest_BindsCorpusQueriesAndRetrievalPolicy()
    {
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "embedding.json");
        await File.WriteAllTextAsync(manifestPath, """
            {
              "schemaVersion": 1,
              "kind": "embeddingRetrieval",
              "name": "DCx retrieval fixture",
              "provenance": { "source": "Synthetic local fixture", "license": "CC0-1.0" },
              "limits": {
                "k": 1,
                "minimumRecallAtK": 1.0,
                "minimumMeanReciprocalRank": 1.0,
                "resources": {
                  "maximumElapsedMilliseconds": 5000,
                  "maximumPeakWorkingSetBytes": 268435456,
                  "maximumPeakGpuBytes": null,
                  "requireUnloadConfirmation": true
                }
              },
              "documents": [
                { "id": "doc-1", "text": "The blue square is above the red circle." },
                { "id": "doc-2", "text": "A separate distractor sentence." }
              ],
              "queries": [
                { "id": "query-1", "text": "Where is the blue square?", "relevantDocumentIds": ["doc-1"] }
              ]
            }
            """);

        var review = Assert.IsType<EmbeddingBenchmarkManifestReview>(
            await LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));

        Assert.Equal(LocalModelBenchmarkKind.EmbeddingRetrieval, review.Kind);
        Assert.Equal(2, review.Documents.Count);
        Assert.Single(review.Queries);
        Assert.Equal(1, review.Limits.K);
        Assert.Equal(64, review.FixtureSuiteSha256.Length);
        Assert.Equal(64, review.PolicySha256.Length);
    }

    [Theory]
    [InlineData("\"name\": \"duplicate\",", "duplicate")]
    [InlineData("\"unexpected\": true,", "unknown")]
    public async Task Manifest_DeniesDuplicateAndUnknownMembers(string insertion, string _)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "speech.wav"), WaveBytes());
        var manifestPath = Path.Combine(root, "invalid.json");
        var json = TranscriptionManifest().Replace(
            "\"name\": \"DCx local speech fixture\",",
            $"\"name\": \"DCx local speech fixture\", {insertion}",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, json);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));
    }

    [Fact]
    public async Task TranscriptionManifest_DeniesWaveTraversalAndDetectsMutation()
    {
        Directory.CreateDirectory(root);
        var outside = Path.Combine(Path.GetDirectoryName(root)!, $"outside-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(outside, WaveBytes());
        try
        {
            var traversalPath = Path.Combine(root, "traversal.json");
            await File.WriteAllTextAsync(
                traversalPath,
                TranscriptionManifest().Replace("speech.wav", $"../{Path.GetFileName(outside)}", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalModelBenchmarkManifestLoader.LoadAsync(traversalPath));

            var wavePath = Path.Combine(root, "speech.wav");
            await File.WriteAllBytesAsync(wavePath, WaveBytes());
            var manifestPath = Path.Combine(root, "transcription.json");
            await File.WriteAllTextAsync(manifestPath, TranscriptionManifest());
            var first = Assert.IsType<TranscriptionBenchmarkManifestReview>(
                await LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));
            await File.WriteAllBytesAsync(wavePath, [.. WaveBytes(), 0]);
            var second = Assert.IsType<TranscriptionBenchmarkManifestReview>(
                await LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));

            Assert.Equal(first.ManifestSha256, second.ManifestSha256);
            Assert.NotEqual(first.FixtureSuiteSha256, second.FixtureSuiteSha256);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TranscriptionManifest_DeniesDuplicateWaveWeighting()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "speech.wav"), WaveBytes());
        var manifestPath = Path.Combine(root, "duplicate-wave.json");
        var duplicate = """
            ,
                { "id": "speech-2", "waveFile": "speech.wav", "expectedText": "DCx transcription fixture 42." }
            """;
        await File.WriteAllTextAsync(
            manifestPath,
            TranscriptionManifest().Replace(
                "\n  ]\n}",
                duplicate + "\n  ]\n}",
                StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelBenchmarkManifestLoader.LoadAsync(manifestPath));
    }

    [Fact]
    public async Task EvidenceStore_PersistsOnlyLatestAggregateEvidenceAtomically()
    {
        Directory.CreateDirectory(root);
        var store = await LocalModelBenchmarkEvidenceStore.OpenAsync(Path.Combine(root, "data"));
        var evidence = Evidence(0.1);

        await store.SaveTranscriptionAsync(evidence);
        await store.SaveTranscriptionAsync(Evidence(0.2));

        var files = Directory.GetFiles(Path.GetDirectoryName(store.LatestTranscriptionEvidencePath)!);
        Assert.Equal([store.LatestTranscriptionEvidencePath], files);
        var json = await File.ReadAllTextAsync(store.LatestTranscriptionEvidencePath);
        Assert.DoesNotContain("private transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(0.2, document.RootElement.GetProperty("result").GetProperty("wordErrorRate").GetDouble());
    }

    [Fact]
    public async Task EvidenceStore_RejectsDirectorySwappedForLinkAfterOpen()
    {
        var dataRoot = Path.Combine(root, "data");
        var store = await LocalModelBenchmarkEvidenceStore.OpenAsync(dataRoot);
        var evidenceRoot = Path.GetDirectoryName(store.LatestTranscriptionEvidencePath)!;
        Directory.Move(evidenceRoot, evidenceRoot + "-original");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(evidenceRoot, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveTranscriptionAsync(Evidence(0.1)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task EvidenceStore_RejectsLinkedTargetWithoutChangingOutsideFile()
    {
        var dataRoot = Path.Combine(root, "data");
        var evidenceRoot = Path.Combine(dataRoot, "local-models", "benchmark-evidence");
        Directory.CreateDirectory(evidenceRoot);
        var outside = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(evidenceRoot, "transcription-latest.json"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalModelBenchmarkEvidenceStore.OpenAsync(dataRoot));

        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static TranscriptionBenchmarkEvidence Evidence(double wordErrorRate) =>
        new(
            1,
            Digest('1'),
            Digest('2'),
            Digest('3'),
            Digest('4'),
            new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero),
            new TranscriptionAccuracyResult(
                true,
                1,
                10,
                0,
                0,
                0,
                wordErrorRate,
                20,
                0,
                0,
                new LocalBenchmarkResourceResult(true, 10, 1024, null, true, []),
                []));

    private static string TranscriptionManifest() => """
        {
          "schemaVersion": 1,
          "kind": "transcription",
          "name": "DCx local speech fixture",
          "provenance": { "source": "Synthetic local fixture", "license": "CC0-1.0" },
          "limits": {
            "maximumWordErrorRate": 0.1,
            "maximumCharacterErrorRate": 0.05,
            "resources": {
              "maximumElapsedMilliseconds": 5000,
              "maximumPeakWorkingSetBytes": 268435456,
              "maximumPeakGpuBytes": null,
              "requireUnloadConfirmation": true
            }
          },
          "fixtures": [
            { "id": "speech-1", "waveFile": "speech.wav", "expectedText": "DCx transcription fixture 42." }
          ]
        }
        """;

    private static byte[] WaveBytes() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E',
    ];

    private static string Digest(char character) => new(character, 64);
}
