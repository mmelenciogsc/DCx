using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public enum LocalModelBenchmarkKind
{
    Transcription,
    EmbeddingRetrieval,
}

public sealed record LocalModelBenchmarkIdentityPart(string Name, string Sha256);

public sealed record LocalModelBenchmarkApproval(
    LocalModelBenchmarkKind Kind,
    string BackendBindingSha256,
    string FixtureSuiteSha256,
    string PolicySha256,
    DateTimeOffset ReviewedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record TranscriptionBenchmarkFixture(string Id, string WavePath, string ExpectedText);

public sealed record EmbeddingBenchmarkDocument(string Id, string Text);

public sealed record EmbeddingBenchmarkQuery(
    string Id,
    string Text,
    IReadOnlyList<string> RelevantDocumentIds);

public sealed record TranscriptionBenchmarkBackendResult(
    string Text,
    LocalBenchmarkResourceObservation Resources);

public sealed record EmbeddingBenchmarkBackendResult(
    IReadOnlyList<float[]> Vectors,
    LocalBenchmarkResourceObservation Resources);

public interface ITranscriptionBenchmarkBackend
{
    string BindingSha256 { get; }

    Task<TranscriptionBenchmarkBackendResult> TranscribeAsync(
        string wavePath,
        CancellationToken cancellationToken);
}

public interface IEmbeddingBenchmarkBackend
{
    string BindingSha256 { get; }

    Task<EmbeddingBenchmarkBackendResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

public sealed class WhisperTranscriptionBenchmarkBackend : ITranscriptionBenchmarkBackend
{
    private readonly ApprovedWhisperBackendBinding binding;
    private readonly TimeSpan timeout;
    private readonly string language;

    public WhisperTranscriptionBenchmarkBackend(
        ApprovedWhisperBackendBinding binding,
        TimeSpan timeout,
        string language = "en")
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (language.Length is < 2 or > 16 ||
            language.Any(character => !char.IsAsciiLetter(character) && character != '-'))
        {
            throw new ArgumentException("The benchmark language must be a short ASCII language tag.", nameof(language));
        }

        BindingSha256 = LocalModelBenchmarkRunner.ComputeBackendBindingSha256(
            LocalModelBenchmarkKind.Transcription,
            [
                new("model", binding.ModelSha256),
                new("model_license", binding.ModelLicenseSha256),
                new("runtime", binding.ExecutableSha256),
                new("runtime_license", binding.RuntimeLicenseSha256),
            ]);
        this.binding = binding;
        this.timeout = timeout;
        this.language = language;
    }

    public string BindingSha256 { get; }

    public async Task<TranscriptionBenchmarkBackendResult> TranscribeAsync(
        string wavePath,
        CancellationToken cancellationToken)
    {
        var result = await binding.CreateTranscriber(timeout)
            .TranscribeWavAsync(wavePath, language, cancellationToken)
            .ConfigureAwait(false);
        return new TranscriptionBenchmarkBackendResult(
            result.Text,
            new LocalBenchmarkResourceObservation(
                result.Elapsed,
                result.PeakWorkingSetBytes,
                PeakGpuBytes: null,
                UnloadConfirmed: true));
    }
}

public sealed class OllamaEmbeddingBenchmarkBackend : IEmbeddingBenchmarkBackend, IDisposable
{
    private readonly ApprovedLocalModelBinding binding;
    private readonly OllamaLocalClient client;
    private bool disposed;

    private OllamaEmbeddingBenchmarkBackend(
        ApprovedLocalModelBinding binding,
        OllamaLocalClient client,
        string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(binding);
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        this.binding = binding;
        RuntimeVersion = runtimeVersion;
        BindingSha256 = LocalModelBenchmarkRunner.ComputeBackendBindingSha256(
            LocalModelBenchmarkKind.EmbeddingRetrieval,
            [
                new("model", binding.Digest),
                new("model_license", binding.LicenseSha256),
                new("model_name", Sha256(binding.Name)),
                new("runtime_version", Sha256(runtimeVersion)),
            ]);
    }

    public string BindingSha256 { get; }
    public string RuntimeVersion { get; }

    public static async Task<OllamaEmbeddingBenchmarkBackend> CreateAsync(
        ApprovedLocalModelBinding binding,
        Uri origin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var client = new OllamaLocalClient(origin, timeout);
        try
        {
            return await CreateAsync(binding, client, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<OllamaEmbeddingBenchmarkBackend> CreateAsync(
        ApprovedLocalModelBinding binding,
        OllamaLocalClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(client);
        await client.ValidatePinnedEmbeddingBindingAsync(
            binding.Name,
            binding.Digest,
            binding.LicenseSha256,
            cancellationToken).ConfigureAwait(false);
        var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        return new OllamaEmbeddingBenchmarkBackend(binding, client, version);
    }

    public async Task<EmbeddingBenchmarkBackendResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = await client.EmbedPinnedForBenchmarkAsync(
            binding.Name,
            binding.Digest,
            binding.LicenseSha256,
            RuntimeVersion,
            inputs,
            cancellationToken).ConfigureAwait(false);
        return new EmbeddingBenchmarkBackendResult(result.Embedding.Embeddings, result.Resources);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record TranscriptionBenchmarkEvidence(
    int SchemaVersion,
    string ApprovalSha256,
    string BackendBindingSha256,
    string FixtureSuiteSha256,
    string PolicySha256,
    DateTimeOffset CapturedAtUtc,
    TranscriptionAccuracyResult Result);

public sealed record EmbeddingBenchmarkEvidence(
    int SchemaVersion,
    string ApprovalSha256,
    string BackendBindingSha256,
    string FixtureSuiteSha256,
    string PolicySha256,
    DateTimeOffset CapturedAtUtc,
    EmbeddingRetrievalResult Result);

public static class LocalModelBenchmarkRunner
{
    private const int EvidenceSchemaVersion = 1;
    private const int MaximumIdentityParts = 8;
    private const int MaximumTranscriptionFixtures = 32;
    private const int MaximumEmbeddingDocuments = 256;
    private const int MaximumEmbeddingQueries = 64;
    private const int MaximumEmbeddingBatch = 32;
    private const int MaximumBenchmarkTextCharacters = 8192;
    private const long MaximumWaveBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan MaximumApprovalWindow = TimeSpan.FromMinutes(30);

    public static string ComputeBackendBindingSha256(
        LocalModelBenchmarkKind kind,
        IReadOnlyList<LocalModelBenchmarkIdentityPart> identityParts)
    {
        ArgumentNullException.ThrowIfNull(identityParts);
        if (!Enum.IsDefined(kind) || identityParts.Count is 0 or > MaximumIdentityParts)
        {
            throw new ArgumentException("A benchmark backend binding requires 1 to 8 exact identity parts.", nameof(identityParts));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var digest = new BenchmarkDigestBuilder("dcx-local-model-backend-v1");
        digest.AddInt32((int)kind);
        foreach (var part in identityParts.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(part);
            ValidateToken(part.Name, nameof(identityParts));
            ValidateDigest(part.Sha256, nameof(identityParts));
            if (!names.Add(part.Name))
            {
                throw new ArgumentException("Benchmark backend identity-part names must be unique.", nameof(identityParts));
            }
            digest.AddString(part.Name);
            digest.AddString(part.Sha256.ToLowerInvariant());
        }
        return digest.Complete();
    }

    public static async Task<string> ComputeTranscriptionSuiteSha256Async(
        IReadOnlyList<TranscriptionBenchmarkFixture> fixtures,
        CancellationToken cancellationToken = default)
    {
        var leases = await OpenTranscriptionFixturesAsync(fixtures, cancellationToken).ConfigureAwait(false);
        try
        {
            return ComputeTranscriptionSuiteSha256(leases);
        }
        finally
        {
            await DisposeLeasesAsync(leases).ConfigureAwait(false);
        }
    }

    public static string ComputeEmbeddingSuiteSha256(
        IReadOnlyList<EmbeddingBenchmarkDocument> documents,
        IReadOnlyList<EmbeddingBenchmarkQuery> queries)
    {
        var validated = ValidateEmbeddingSuite(documents, queries);
        using var digest = new BenchmarkDigestBuilder("dcx-embedding-suite-v1");
        digest.AddInt32(validated.Documents.Length);
        foreach (var document in validated.Documents)
        {
            digest.AddString(document.Id);
            digest.AddString(document.Text);
        }
        digest.AddInt32(validated.Queries.Length);
        foreach (var query in validated.Queries)
        {
            digest.AddString(query.Id);
            digest.AddString(query.Text);
            var relevant = query.RelevantDocumentIds.Order(StringComparer.Ordinal).ToArray();
            digest.AddInt32(relevant.Length);
            foreach (var id in relevant) digest.AddString(id);
        }
        return digest.Complete();
    }

    public static string ComputeTranscriptionPolicySha256(TranscriptionAccuracyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(limits.Resources);
        if (!double.IsFinite(limits.MaximumWordErrorRate) || limits.MaximumWordErrorRate is < 0 or > 1 ||
            !double.IsFinite(limits.MaximumCharacterErrorRate) || limits.MaximumCharacterErrorRate is < 0 or > 1 ||
            !limits.Resources.RequireUnloadConfirmation)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Transcription accuracy limits must be finite ratios from zero through one.");
        }
        using var digest = new BenchmarkDigestBuilder("dcx-transcription-policy-v1");
        AddResourceLimits(digest, limits.Resources);
        digest.AddDouble(limits.MaximumWordErrorRate);
        digest.AddDouble(limits.MaximumCharacterErrorRate);
        return digest.Complete();
    }

    public static string ComputeEmbeddingPolicySha256(EmbeddingRetrievalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(limits.Resources);
        if (limits.K is <= 0 or > 32 ||
            !double.IsFinite(limits.MinimumRecallAtK) || limits.MinimumRecallAtK is < 0 or > 1 ||
            !double.IsFinite(limits.MinimumMeanReciprocalRank) || limits.MinimumMeanReciprocalRank is < 0 or > 1 ||
            !limits.Resources.RequireUnloadConfirmation)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Embedding retrieval limits are invalid.");
        }
        using var digest = new BenchmarkDigestBuilder("dcx-embedding-policy-v1");
        AddResourceLimits(digest, limits.Resources);
        digest.AddInt32(limits.K);
        digest.AddDouble(limits.MinimumRecallAtK);
        digest.AddDouble(limits.MinimumMeanReciprocalRank);
        return digest.Complete();
    }

    public static Task<TranscriptionBenchmarkEvidence> RunTranscriptionAsync(
        LocalModelBenchmarkApproval approval,
        bool explicitlyApproved,
        IReadOnlyList<TranscriptionBenchmarkFixture> fixtures,
        TranscriptionAccuracyLimits limits,
        ITranscriptionBenchmarkBackend backend,
        CancellationToken cancellationToken = default) =>
        RunTranscriptionAtAsync(
            approval,
            explicitlyApproved,
            fixtures,
            limits,
            backend,
            DateTimeOffset.UtcNow,
            cancellationToken);

    internal static async Task<TranscriptionBenchmarkEvidence> RunTranscriptionAtAsync(
        LocalModelBenchmarkApproval approval,
        bool explicitlyApproved,
        IReadOnlyList<TranscriptionBenchmarkFixture> fixtures,
        TranscriptionAccuracyLimits limits,
        ITranscriptionBenchmarkBackend backend,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(backend);
        ValidateApproval(approval, LocalModelBenchmarkKind.Transcription, explicitlyApproved, capturedAtUtc);
        RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
        var policySha256 = ComputeTranscriptionPolicySha256(limits);
        RequireDigestMatch(policySha256, approval.PolicySha256, "The reviewed transcription benchmark policy changed.");

        var leases = await OpenTranscriptionFixturesAsync(fixtures, cancellationToken).ConfigureAwait(false);
        try
        {
            var suiteSha256 = ComputeTranscriptionSuiteSha256(leases);
            RequireDigestMatch(suiteSha256, approval.FixtureSuiteSha256, "The reviewed transcription fixture suite changed.");
            var samples = new List<TranscriptionAccuracySample>(leases.Count);
            foreach (var lease in leases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
                var output = await backend.TranscribeAsync(lease.Path, cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(output);
                RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
                LocalBenchmarkResourceEvaluator.ValidateObservation(output.Resources);
                samples.Add(new TranscriptionAccuracySample(
                    lease.Fixture.Id,
                    lease.Fixture.ExpectedText,
                    output.Text,
                    output.Resources));
            }

            var result = TranscriptionAccuracyEvaluator.Evaluate(samples, limits);
            return new TranscriptionBenchmarkEvidence(
                EvidenceSchemaVersion,
                ComputeApprovalSha256(approval),
                approval.BackendBindingSha256.ToLowerInvariant(),
                suiteSha256,
                policySha256,
                capturedAtUtc,
                result);
        }
        finally
        {
            await DisposeLeasesAsync(leases).ConfigureAwait(false);
        }
    }

    public static Task<EmbeddingBenchmarkEvidence> RunEmbeddingAsync(
        LocalModelBenchmarkApproval approval,
        bool explicitlyApproved,
        IReadOnlyList<EmbeddingBenchmarkDocument> documents,
        IReadOnlyList<EmbeddingBenchmarkQuery> queries,
        EmbeddingRetrievalLimits limits,
        IEmbeddingBenchmarkBackend backend,
        CancellationToken cancellationToken = default) =>
        RunEmbeddingAtAsync(
            approval,
            explicitlyApproved,
            documents,
            queries,
            limits,
            backend,
            DateTimeOffset.UtcNow,
            cancellationToken);

    internal static async Task<EmbeddingBenchmarkEvidence> RunEmbeddingAtAsync(
        LocalModelBenchmarkApproval approval,
        bool explicitlyApproved,
        IReadOnlyList<EmbeddingBenchmarkDocument> documents,
        IReadOnlyList<EmbeddingBenchmarkQuery> queries,
        EmbeddingRetrievalLimits limits,
        IEmbeddingBenchmarkBackend backend,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(backend);
        ValidateApproval(approval, LocalModelBenchmarkKind.EmbeddingRetrieval, explicitlyApproved, capturedAtUtc);
        RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
        var policySha256 = ComputeEmbeddingPolicySha256(limits);
        RequireDigestMatch(policySha256, approval.PolicySha256, "The reviewed embedding benchmark policy changed.");
        var validated = ValidateEmbeddingSuite(documents, queries);
        if (limits.K > validated.Documents.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Embedding retrieval K exceeds the reviewed document count.");
        }
        var suiteSha256 = ComputeEmbeddingSuiteSha256(validated.Documents, validated.Queries);
        RequireDigestMatch(suiteSha256, approval.FixtureSuiteSha256, "The reviewed embedding fixture suite changed.");

        var inputs = validated.Documents.Select(item => item.Text)
            .Concat(validated.Queries.Select(item => item.Text))
            .ToArray();
        var vectors = new List<float[]>(inputs.Length);
        var observations = new List<LocalBenchmarkResourceObservation>();
        for (var offset = 0; offset < inputs.Length; offset += MaximumEmbeddingBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
            var batch = inputs.Skip(offset).Take(MaximumEmbeddingBatch).ToArray();
            var output = await backend.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(output);
            RequireBinding(backend.BindingSha256, approval.BackendBindingSha256);
            if (output.Vectors is null || output.Vectors.Count != batch.Length)
            {
                throw new InvalidOperationException("The embedding benchmark backend returned the wrong vector count.");
            }
            LocalBenchmarkResourceEvaluator.ValidateObservation(output.Resources);
            vectors.AddRange(output.Vectors);
            observations.Add(output.Resources);
        }

        var documentVectors = validated.Documents
            .Select((item, index) => new EmbeddingRetrievalDocument(item.Id, vectors[index]))
            .ToArray();
        var queryVectors = validated.Queries
            .Select((item, index) => new EmbeddingRetrievalQuery(
                item.Id,
                vectors[validated.Documents.Length + index],
                item.RelevantDocumentIds))
            .ToArray();
        var result = EmbeddingRetrievalEvaluator.Evaluate(
            documentVectors,
            queryVectors,
            AggregateResources(observations),
            limits);
        return new EmbeddingBenchmarkEvidence(
            EvidenceSchemaVersion,
            ComputeApprovalSha256(approval),
            approval.BackendBindingSha256.ToLowerInvariant(),
            suiteSha256,
            policySha256,
            capturedAtUtc,
            result);
    }

    private static async Task<IReadOnlyList<TranscriptionFixtureLease>> OpenTranscriptionFixturesAsync(
        IReadOnlyList<TranscriptionBenchmarkFixture> fixtures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        if (fixtures.Count is 0 or > MaximumTranscriptionFixtures)
        {
            throw new ArgumentException("A transcription benchmark requires 1 to 32 fixtures.", nameof(fixtures));
        }

        var ordered = fixtures.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var leases = new List<TranscriptionFixtureLease>(ordered.Length);
        try
        {
            foreach (var fixture in ordered)
            {
                ArgumentNullException.ThrowIfNull(fixture);
                ValidateToken(fixture.Id, nameof(fixtures));
                if (!ids.Add(fixture.Id))
                {
                    throw new ArgumentException("Transcription fixture identifiers must be unique.", nameof(fixtures));
                }
                ValidateText(fixture.ExpectedText, nameof(fixtures));
                leases.Add(await TranscriptionFixtureLease.OpenAsync(fixture, cancellationToken).ConfigureAwait(false));
            }
            return leases;
        }
        catch
        {
            await DisposeLeasesAsync(leases).ConfigureAwait(false);
            throw;
        }
    }

    private static string ComputeTranscriptionSuiteSha256(IReadOnlyList<TranscriptionFixtureLease> fixtures)
    {
        using var digest = new BenchmarkDigestBuilder("dcx-transcription-suite-v1");
        digest.AddInt32(fixtures.Count);
        foreach (var fixture in fixtures)
        {
            digest.AddString(fixture.Fixture.Id);
            digest.AddString(fixture.Fixture.ExpectedText);
            digest.AddString(fixture.WaveSha256);
            digest.AddInt64(fixture.Length);
        }
        return digest.Complete();
    }

    private static (EmbeddingBenchmarkDocument[] Documents, EmbeddingBenchmarkQuery[] Queries) ValidateEmbeddingSuite(
        IReadOnlyList<EmbeddingBenchmarkDocument> documents,
        IReadOnlyList<EmbeddingBenchmarkQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(queries);
        if (documents.Count is 0 or > MaximumEmbeddingDocuments || queries.Count is 0 or > MaximumEmbeddingQueries)
        {
            throw new ArgumentException("An embedding benchmark requires bounded nonempty document and query sets.");
        }

        var orderedDocuments = documents.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in orderedDocuments)
        {
            ArgumentNullException.ThrowIfNull(document);
            ValidateToken(document.Id, nameof(documents));
            ValidateText(document.Text, nameof(documents));
            if (!documentIds.Add(document.Id))
            {
                throw new ArgumentException("Embedding document identifiers must be unique.", nameof(documents));
            }
        }

        var orderedQueries = queries.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in orderedQueries)
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateToken(query.Id, nameof(queries));
            ValidateText(query.Text, nameof(queries));
            if (!queryIds.Add(query.Id) || query.RelevantDocumentIds is null ||
                query.RelevantDocumentIds.Count is 0 or > MaximumEmbeddingDocuments ||
                query.RelevantDocumentIds.Distinct(StringComparer.Ordinal).Count() != query.RelevantDocumentIds.Count ||
                query.RelevantDocumentIds.Any(id => !documentIds.Contains(id)))
            {
                throw new ArgumentException("Embedding query identity or relevance labels are invalid.", nameof(queries));
            }
        }
        return (orderedDocuments, orderedQueries);
    }

    private static LocalBenchmarkResourceObservation AggregateResources(
        IReadOnlyList<LocalBenchmarkResourceObservation> observations)
    {
        if (observations.Count == 0) throw new InvalidOperationException("No benchmark resource observation was returned.");
        var elapsedTicks = observations.Aggregate(0L, (total, item) => checked(total + item.Elapsed.Ticks));
        var workingSetComplete = observations.All(item => item.PeakWorkingSetBytes is not null);
        var gpuComplete = observations.All(item => item.PeakGpuBytes is not null);
        return new LocalBenchmarkResourceObservation(
            TimeSpan.FromTicks(elapsedTicks),
            workingSetComplete ? observations.Max(item => item.PeakWorkingSetBytes) : null,
            gpuComplete ? observations.Max(item => item.PeakGpuBytes) : null,
            observations.All(item => item.UnloadConfirmed));
    }

    private static void ValidateApproval(
        LocalModelBenchmarkApproval approval,
        LocalModelBenchmarkKind expectedKind,
        bool explicitlyApproved,
        DateTimeOffset capturedAtUtc)
    {
        if (!explicitlyApproved) throw new InvalidOperationException("Explicit benchmark approval is required.");
        ValidateUtc(approval.ReviewedAtUtc, nameof(approval));
        ValidateUtc(approval.ExpiresAtUtc, nameof(approval));
        ValidateUtc(capturedAtUtc, nameof(capturedAtUtc));
        ValidateDigest(approval.BackendBindingSha256, nameof(approval));
        ValidateDigest(approval.FixtureSuiteSha256, nameof(approval));
        ValidateDigest(approval.PolicySha256, nameof(approval));
        if (approval.Kind != expectedKind || approval.ExpiresAtUtc <= approval.ReviewedAtUtc ||
            approval.ExpiresAtUtc - approval.ReviewedAtUtc > MaximumApprovalWindow ||
            capturedAtUtc < approval.ReviewedAtUtc || capturedAtUtc > approval.ExpiresAtUtc)
        {
            throw new InvalidOperationException("The benchmark approval is wrong, expired, or outside its bounded review window.");
        }
    }

    private static string ComputeApprovalSha256(LocalModelBenchmarkApproval approval)
    {
        using var digest = new BenchmarkDigestBuilder("dcx-local-model-benchmark-approval-v1");
        digest.AddInt32((int)approval.Kind);
        digest.AddString(approval.BackendBindingSha256.ToLowerInvariant());
        digest.AddString(approval.FixtureSuiteSha256.ToLowerInvariant());
        digest.AddString(approval.PolicySha256.ToLowerInvariant());
        digest.AddInt64(approval.ReviewedAtUtc.UtcTicks);
        digest.AddInt64(approval.ExpiresAtUtc.UtcTicks);
        return digest.Complete();
    }

    private static void AddResourceLimits(BenchmarkDigestBuilder digest, LocalBenchmarkResourceLimits limits)
    {
        LocalBenchmarkResourceEvaluator.ValidateLimits(limits);
        digest.AddInt64(limits.MaximumElapsed.Ticks);
        digest.AddInt64(limits.MaximumPeakWorkingSetBytes);
        digest.AddBoolean(limits.MaximumPeakGpuBytes is not null);
        if (limits.MaximumPeakGpuBytes is not null) digest.AddInt64(limits.MaximumPeakGpuBytes.Value);
        digest.AddBoolean(limits.RequireUnloadConfirmation);
    }

    private static void RequireBinding(string actual, string expected)
    {
        ValidateDigest(actual, nameof(actual));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The exact approved benchmark backend binding changed.");
        }
    }

    private static void RequireDigestMatch(string actual, string expected, string message)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void ValidateDigest(string? digest, string parameterName)
    {
        if (digest is not { Length: 64 } || digest.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Benchmark digests must be 64 hexadecimal characters.", parameterName);
        }
    }

    private static void ValidateToken(string? value, string parameterName)
    {
        if (value is not { Length: > 0 and <= 64 } ||
            !IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("Benchmark identifiers must be bounded ASCII tokens.", parameterName);
        }
    }

    private static void ValidateText(string? text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumBenchmarkTextCharacters || text.Contains('\0') ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Benchmark text is missing or outside its safety bounds.", parameterName);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Benchmark review and capture times must be UTC.", parameterName);
        }
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static async ValueTask DisposeLeasesAsync(IEnumerable<TranscriptionFixtureLease> leases)
    {
        foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TranscriptionFixtureLease : IAsyncDisposable
    {
        private readonly FileStream stream;

        private TranscriptionFixtureLease(
            TranscriptionBenchmarkFixture fixture,
            string path,
            long length,
            string waveSha256,
            FileStream stream)
        {
            Fixture = fixture;
            Path = path;
            Length = length;
            WaveSha256 = waveSha256;
            this.stream = stream;
        }

        public TranscriptionBenchmarkFixture Fixture { get; }
        public string Path { get; }
        public long Length { get; }
        public string WaveSha256 { get; }

        public static async Task<TranscriptionFixtureLease> OpenAsync(
            TranscriptionBenchmarkFixture fixture,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fixture.WavePath);
            var canonical = System.IO.Path.GetFullPath(fixture.WavePath);
            if (!System.IO.Path.IsPathFullyQualified(canonical) || canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(System.IO.Path.GetExtension(canonical), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A benchmark audio fixture must be an absolute local WAV path.", nameof(fixture));
            }
            RejectReparseChain(canonical);
            var file = new FileInfo(canonical);
            if (!file.Exists || file.Length is < 12 or > MaximumWaveBytes ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("A benchmark audio fixture is missing or outside its safety bounds.", nameof(fixture));
            }

            var stream = new FileStream(
                canonical,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                if (stream.Length is < 12 or > MaximumWaveBytes)
                {
                    throw new ArgumentException("A benchmark audio fixture changed outside its safety bounds.", nameof(fixture));
                }
                var header = new byte[12];
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                if (!header.AsSpan().StartsWith("RIFF"u8) || !header.AsSpan(8).StartsWith("WAVE"u8))
                {
                    throw new ArgumentException("A benchmark audio fixture has no WAV signature.", nameof(fixture));
                }
                stream.Position = 0;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                stream.Position = 0;
                return new TranscriptionFixtureLease(fixture, canonical, stream.Length, hash, stream);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();

        private static void RejectReparseChain(string path)
        {
            var current = new FileInfo(path).Directory;
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException("Benchmark fixture paths cannot traverse reparse points.", nameof(path));
                }
                current = current.Parent;
            }
        }
    }

    private sealed class BenchmarkDigestBuilder : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool completed;

        public BenchmarkDigestBuilder(string domain) => AddString(domain);

        public void AddString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                AddInt32(bytes.Length);
                hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void AddBoolean(bool value) => hash.AppendData([value ? (byte)1 : (byte)0]);

        public void AddInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            hash.AppendData(bytes);
        }

        public void AddInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            hash.AppendData(bytes);
        }

        public void AddDouble(double value) => AddInt64(BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value));

        public string Complete()
        {
            ObjectDisposedException.ThrowIf(completed, this);
            completed = true;
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public void Dispose() => hash.Dispose();
    }
}
