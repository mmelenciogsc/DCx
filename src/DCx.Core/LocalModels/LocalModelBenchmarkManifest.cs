using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public sealed record LocalModelBenchmarkProvenance(string Source, string License);

public interface ILocalModelBenchmarkManifestReview
{
    LocalModelBenchmarkKind Kind { get; }
    string ManifestPath { get; }
    string ManifestSha256 { get; }
    string Name { get; }
    LocalModelBenchmarkProvenance Provenance { get; }
    string FixtureSuiteSha256 { get; }
    string PolicySha256 { get; }
}

public sealed record TranscriptionBenchmarkManifestReview(
    string ManifestPath,
    string ManifestSha256,
    string Name,
    LocalModelBenchmarkProvenance Provenance,
    IReadOnlyList<TranscriptionBenchmarkFixture> Fixtures,
    TranscriptionAccuracyLimits Limits,
    string FixtureSuiteSha256,
    string PolicySha256) : ILocalModelBenchmarkManifestReview
{
    public LocalModelBenchmarkKind Kind => LocalModelBenchmarkKind.Transcription;
}

public sealed record EmbeddingBenchmarkManifestReview(
    string ManifestPath,
    string ManifestSha256,
    string Name,
    LocalModelBenchmarkProvenance Provenance,
    IReadOnlyList<EmbeddingBenchmarkDocument> Documents,
    IReadOnlyList<EmbeddingBenchmarkQuery> Queries,
    EmbeddingRetrievalLimits Limits,
    string FixtureSuiteSha256,
    string PolicySha256) : ILocalModelBenchmarkManifestReview
{
    public LocalModelBenchmarkKind Kind => LocalModelBenchmarkKind.EmbeddingRetrieval;
}

public static class LocalModelBenchmarkManifestLoader
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static async Task<ILocalModelBenchmarkManifestReview> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var canonical = ValidateManifestPath(manifestPath);
        await using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidOperationException("The benchmark manifest is empty or exceeds its size limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("kind", out var kindElement) ||
                kindElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("The benchmark manifest kind is missing or invalid.");
            }

            return kindElement.GetString() switch
            {
                "transcription" => await LoadTranscriptionAsync(
                    canonical, manifestSha256, bytes, cancellationToken).ConfigureAwait(false),
                "embeddingRetrieval" => LoadEmbedding(canonical, manifestSha256, bytes),
                _ => throw new InvalidOperationException("The benchmark manifest kind is unsupported."),
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The benchmark manifest is invalid JSON or violates its strict schema.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<TranscriptionBenchmarkManifestReview> LoadTranscriptionAsync(
        string canonical,
        string manifestSha256,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<TranscriptionManifestDto>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("The transcription benchmark manifest is empty.");
        ValidateCommon(manifest.SchemaVersion, manifest.Kind, "transcription", manifest.Name, manifest.Provenance);
        if (manifest.Fixtures is null)
        {
            throw new InvalidOperationException("The transcription fixture list is missing.");
        }

        var directory = Path.GetDirectoryName(canonical)
            ?? throw new InvalidOperationException("The benchmark manifest directory is unavailable.");
        var fixtures = manifest.Fixtures.Select(fixture =>
        {
            if (fixture is null)
            {
                throw new InvalidOperationException("A transcription fixture is missing.");
            }
            return new TranscriptionBenchmarkFixture(
                fixture.Id ?? string.Empty,
                ResolveRelativeWave(directory, fixture.WaveFile),
                fixture.ExpectedText ?? string.Empty);
        }).ToArray();
        if (fixtures.Select(fixture => fixture.WavePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != fixtures.Length)
        {
            throw new InvalidOperationException("Transcription benchmark WAV paths must be unique.");
        }
        var limits = CreateResourceLimits(manifest.Limits?.Resources);
        var accuracyLimits = new TranscriptionAccuracyLimits(
            manifest.Limits?.MaximumWordErrorRate ?? double.NaN,
            manifest.Limits?.MaximumCharacterErrorRate ?? double.NaN,
            limits);
        var suiteSha256 = await LocalModelBenchmarkRunner
            .ComputeTranscriptionSuiteSha256Async(fixtures, cancellationToken)
            .ConfigureAwait(false);
        var policySha256 = LocalModelBenchmarkRunner.ComputeTranscriptionPolicySha256(accuracyLimits);
        return new TranscriptionBenchmarkManifestReview(
            canonical,
            manifestSha256,
            manifest.Name!,
            CreateProvenance(manifest.Provenance!),
            fixtures,
            accuracyLimits,
            suiteSha256,
            policySha256);
    }

    private static EmbeddingBenchmarkManifestReview LoadEmbedding(
        string canonical,
        string manifestSha256,
        byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<EmbeddingManifestDto>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("The embedding benchmark manifest is empty.");
        ValidateCommon(manifest.SchemaVersion, manifest.Kind, "embeddingRetrieval", manifest.Name, manifest.Provenance);
        if (manifest.Documents is null || manifest.Queries is null)
        {
            throw new InvalidOperationException("The embedding document or query list is missing.");
        }

        var documents = manifest.Documents.Select(document =>
            document is null
                ? throw new InvalidOperationException("An embedding document is missing.")
                : new EmbeddingBenchmarkDocument(document.Id ?? string.Empty, document.Text ?? string.Empty))
            .ToArray();
        var queries = manifest.Queries.Select(query =>
            query is null
                ? throw new InvalidOperationException("An embedding query is missing.")
                : new EmbeddingBenchmarkQuery(
                    query.Id ?? string.Empty,
                    query.Text ?? string.Empty,
                    query.RelevantDocumentIds ?? []))
            .ToArray();
        var limits = new EmbeddingRetrievalLimits(
            manifest.Limits?.K ?? 0,
            manifest.Limits?.MinimumRecallAtK ?? double.NaN,
            manifest.Limits?.MinimumMeanReciprocalRank ?? double.NaN,
            CreateResourceLimits(manifest.Limits?.Resources));
        var suiteSha256 = LocalModelBenchmarkRunner.ComputeEmbeddingSuiteSha256(documents, queries);
        var policySha256 = LocalModelBenchmarkRunner.ComputeEmbeddingPolicySha256(limits);
        if (limits.K > documents.Length)
        {
            throw new InvalidOperationException("Embedding retrieval K exceeds the document count.");
        }
        return new EmbeddingBenchmarkManifestReview(
            canonical,
            manifestSha256,
            manifest.Name!,
            CreateProvenance(manifest.Provenance!),
            documents,
            queries,
            limits,
            suiteSha256,
            policySha256);
    }

    private static LocalBenchmarkResourceLimits CreateResourceLimits(ResourceLimitsDto? limits)
    {
        if (limits is null)
        {
            throw new InvalidOperationException("Benchmark resource limits are missing.");
        }
        return new LocalBenchmarkResourceLimits(
            TimeSpan.FromMilliseconds(limits.MaximumElapsedMilliseconds),
            limits.MaximumPeakWorkingSetBytes,
            limits.MaximumPeakGpuBytes,
            limits.RequireUnloadConfirmation);
    }

    private static void ValidateCommon(
        int schemaVersion,
        string? actualKind,
        string expectedKind,
        string? name,
        ProvenanceDto? provenance)
    {
        if (schemaVersion != CurrentSchemaVersion || !string.Equals(actualKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The benchmark manifest schema or kind is unsupported.");
        }
        ValidateDisplayText(name, 128, "benchmark name");
        if (provenance is null)
        {
            throw new InvalidOperationException("Benchmark provenance is missing.");
        }
        ValidateDisplayText(provenance.Source, 512, "benchmark source");
        ValidateDisplayText(provenance.License, 256, "benchmark license");
    }

    private static LocalModelBenchmarkProvenance CreateProvenance(ProvenanceDto provenance) =>
        new(provenance.Source!, provenance.License!);

    private static void ValidateDisplayText(string? value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Contains('\0') ||
            value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new InvalidOperationException($"The {label} is missing or outside its safety bounds.");
        }
    }

    private static string ValidateManifestPath(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (manifestPath.Length > 1024 || manifestPath.Contains('\0'))
        {
            throw new ArgumentException("The benchmark manifest path is invalid.", nameof(manifestPath));
        }
        var canonical = Path.GetFullPath(manifestPath.Trim());
        if (!Path.IsPathFullyQualified(canonical) || canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(canonical), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The benchmark manifest must be an absolute local JSON path.", nameof(manifestPath));
        }
        RejectReparseChain(canonical, requireFile: true);
        return canonical;
    }

    private static string ResolveRelativeWave(string manifestDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 || relativePath.Contains('\0') ||
            relativePath.Contains(':') || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("A benchmark WAV path must be a bounded relative path.");
        }
        var root = Path.GetFullPath(manifestDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonical = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(canonical), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A benchmark WAV path escapes its manifest directory or has the wrong extension.");
        }
        RejectReparseChain(canonical, requireFile: true);
        return canonical;
    }

    private static void RejectReparseChain(string path, bool requireFile)
    {
        var file = new FileInfo(path);
        if (requireFile && (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
        {
            throw new InvalidOperationException("A benchmark input must be an existing regular local file.");
        }
        DirectoryInfo? parent = file.Directory;
        while (parent is not null)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Benchmark inputs cannot traverse reparse directories.");
            }
            parent = parent.Parent;
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
                    throw new InvalidOperationException("Benchmark manifests cannot contain duplicate JSON properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private sealed class ProvenanceDto
    {
        public string? Source { get; init; }
        public string? License { get; init; }
    }

    private sealed class ResourceLimitsDto
    {
        public long MaximumElapsedMilliseconds { get; init; }
        public long MaximumPeakWorkingSetBytes { get; init; }
        public long? MaximumPeakGpuBytes { get; init; }
        public bool RequireUnloadConfirmation { get; init; }
    }

    private sealed class TranscriptionLimitsDto
    {
        public double MaximumWordErrorRate { get; init; }
        public double MaximumCharacterErrorRate { get; init; }
        public ResourceLimitsDto? Resources { get; init; }
    }

    private sealed class TranscriptionFixtureDto
    {
        public string? Id { get; init; }
        public string? WaveFile { get; init; }
        public string? ExpectedText { get; init; }
    }

    private sealed class TranscriptionManifestDto
    {
        public int SchemaVersion { get; init; }
        public string? Kind { get; init; }
        public string? Name { get; init; }
        public ProvenanceDto? Provenance { get; init; }
        public TranscriptionLimitsDto? Limits { get; init; }
        public TranscriptionFixtureDto?[]? Fixtures { get; init; }
    }

    private sealed class EmbeddingLimitsDto
    {
        public int K { get; init; }
        public double MinimumRecallAtK { get; init; }
        public double MinimumMeanReciprocalRank { get; init; }
        public ResourceLimitsDto? Resources { get; init; }
    }

    private sealed class EmbeddingDocumentDto
    {
        public string? Id { get; init; }
        public string? Text { get; init; }
    }

    private sealed class EmbeddingQueryDto
    {
        public string? Id { get; init; }
        public string? Text { get; init; }
        public string[]? RelevantDocumentIds { get; init; }
    }

    private sealed class EmbeddingManifestDto
    {
        public int SchemaVersion { get; init; }
        public string? Kind { get; init; }
        public string? Name { get; init; }
        public ProvenanceDto? Provenance { get; init; }
        public EmbeddingLimitsDto? Limits { get; init; }
        public EmbeddingDocumentDto?[]? Documents { get; init; }
        public EmbeddingQueryDto?[]? Queries { get; init; }
    }
}

public sealed class LocalModelBenchmarkEvidenceStore
{
    public const int MaximumEvidenceBytes = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16,
    };

    private readonly string root;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private LocalModelBenchmarkEvidenceStore(string root) => this.root = root;

    public string LatestTranscriptionEvidencePath => Path.Combine(root, "transcription-latest.json");
    public string LatestEmbeddingEvidencePath => Path.Combine(root, "embedding-latest.json");

    public static Task<LocalModelBenchmarkEvidenceStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var applicationRoot = LocalModelStoragePathGuard.EnsureDirectory(
            applicationDataRoot,
            "benchmark application-data");
        var localModelsRoot = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(applicationRoot, "local-models"),
            "benchmark settings");
        var root = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(localModelsRoot, "benchmark-evidence"),
            "benchmark evidence");
        ValidateEvidenceTarget(Path.Combine(root, "transcription-latest.json"));
        ValidateEvidenceTarget(Path.Combine(root, "embedding-latest.json"));
        return Task.FromResult(new LocalModelBenchmarkEvidenceStore(root));
    }

    public Task SaveTranscriptionAsync(
        TranscriptionBenchmarkEvidence evidence,
        CancellationToken cancellationToken = default) =>
        SaveAsync(LatestTranscriptionEvidencePath, evidence, cancellationToken);

    public Task SaveEmbeddingAsync(
        EmbeddingBenchmarkEvidence evidence,
        CancellationToken cancellationToken = default) =>
        SaveAsync(LatestEmbeddingEvidencePath, evidence, cancellationToken);

    private async Task SaveAsync<T>(string path, T evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumEvidenceBytes)
        {
            throw new InvalidOperationException("Aggregate benchmark evidence exceeds its size limit.");
        }
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = Path.Combine(root, $"benchmark-{Guid.NewGuid():N}.tmp");
        try
        {
            LocalModelStoragePathGuard.RequireDirectory(root, "benchmark evidence");
            ValidateEvidenceTarget(path);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            LocalModelStoragePathGuard.RequireDirectory(root, "benchmark evidence");
            LocalModelStoragePathGuard.ValidateFile(temporary, mustExist: true, "benchmark temporary evidence");
            ValidateEvidenceTarget(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            LocalModelStoragePathGuard.TryDeleteTemporary(root, temporary, "benchmark temporary evidence");
            mutationLock.Release();
        }
    }

    private static void ValidateEvidenceTarget(string path)
    {
        LocalModelStoragePathGuard.ValidateFile(path, mustExist: false, "benchmark evidence target");
    }
}
