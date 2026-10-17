using System.Text.Json;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelBenchmarkRunnerTests : IDisposable
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 18, 2, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-model-benchmark-{Guid.NewGuid():N}");

    [Fact]
    public async Task TranscriptionRunner_BindsLeasedFixturesApprovalPolicyAndAggregateOnlyEvidence()
    {
        Directory.CreateDirectory(root);
        var first = await CreateWaveAsync("first.wav");
        var second = await CreateWaveAsync("second.wav");
        var fixtures = new TranscriptionBenchmarkFixture[]
        {
            new("z-last", second, "Beta two"),
            new("a-first", first, "Alpha one"),
        };
        var limits = TranscriptionLimits();
        var binding = Binding(LocalModelBenchmarkKind.Transcription);
        var approval = await TranscriptionApprovalAsync(binding, fixtures, limits);
        var calls = new List<string>();
        var backend = new FakeTranscriptionBackend(binding, (path, _) =>
        {
            calls.Add(Path.GetFileName(path));
            var text = Path.GetFileName(path) == "first.wav" ? "alpha one" : "beta two";
            return Task.FromResult(new TranscriptionBenchmarkBackendResult(text, Resources()));
        });

        var evidence = await LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, true, fixtures, limits, backend, CapturedAt);

        Assert.True(evidence.Result.Passed);
        Assert.Equal(1, evidence.SchemaVersion);
        Assert.Equal(["first.wav", "second.wav"], calls);
        Assert.Equal(approval.BackendBindingSha256, evidence.BackendBindingSha256);
        Assert.Equal(approval.FixtureSuiteSha256, evidence.FixtureSuiteSha256);
        Assert.Equal(approval.PolicySha256, evidence.PolicySha256);
        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("Alpha one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("a-first", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(root, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscriptionRunner_DeniesMissingExpiredOrChangedApproval()
    {
        Directory.CreateDirectory(root);
        var wave = await CreateWaveAsync("fixture.wav");
        var fixtures = new[] { new TranscriptionBenchmarkFixture("fixture", wave, "approved words") };
        var limits = TranscriptionLimits();
        var binding = Binding(LocalModelBenchmarkKind.Transcription);
        var approval = await TranscriptionApprovalAsync(binding, fixtures, limits);
        var backend = new FakeTranscriptionBackend(binding, (_, _) => Task.FromResult(
            new TranscriptionBenchmarkBackendResult("approved words", Resources())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, false, fixtures, limits, backend, CapturedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval with { ExpiresAtUtc = CapturedAt.AddSeconds(-1) },
            true,
            fixtures,
            limits,
            backend,
            CapturedAt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval,
            true,
            fixtures,
            limits with { MaximumWordErrorRate = 0.5 },
            backend,
            CapturedAt));

        await File.WriteAllBytesAsync(wave, WaveBytes(extraByte: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, true, fixtures, limits, backend, CapturedAt));
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task TranscriptionRunner_HoldsFixtureLeaseAndRejectsBindingChange()
    {
        Directory.CreateDirectory(root);
        var wave = await CreateWaveAsync("leased.wav");
        var fixtures = new[] { new TranscriptionBenchmarkFixture("leased", wave, "lease held") };
        var limits = TranscriptionLimits();
        var binding = Binding(LocalModelBenchmarkKind.Transcription);
        var approval = await TranscriptionApprovalAsync(binding, fixtures, limits);
        FakeTranscriptionBackend? backend = null;
        var writeDenied = false;
        backend = new FakeTranscriptionBackend(binding, async (path, _) =>
        {
            try
            {
                await File.WriteAllBytesAsync(path, WaveBytes(extraByte: true));
            }
            catch (IOException)
            {
                writeDenied = true;
            }
            backend!.BindingSha256 = Digest('9');
            return new TranscriptionBenchmarkBackendResult("lease held", Resources());
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, true, fixtures, limits, backend, CapturedAt));
        Assert.True(writeDenied);
    }

    [Fact]
    public async Task TranscriptionRunner_PropagatesCancellationAndReleasesLease()
    {
        Directory.CreateDirectory(root);
        var wave = await CreateWaveAsync("cancel.wav");
        var fixtures = new[] { new TranscriptionBenchmarkFixture("cancel", wave, "cancel me") };
        var limits = TranscriptionLimits();
        var binding = Binding(LocalModelBenchmarkKind.Transcription);
        var approval = await TranscriptionApprovalAsync(binding, fixtures, limits);
        var backend = new FakeTranscriptionBackend(binding, async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new TranscriptionBenchmarkBackendResult("unreachable", Resources());
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, true, fixtures, limits, backend, CapturedAt, cancellation.Token));

        await File.WriteAllBytesAsync(wave, WaveBytes(extraByte: true));
    }

    [Fact]
    public async Task WhisperBackend_ComposesExactApprovedFilesWithRunner()
    {
        Directory.CreateDirectory(root);
        var wave = await CreateWaveAsync("production-adapter.wav");
        var model = Path.Combine(root, "fixture-model.bin");
        var runtimeLicense = Path.Combine(root, "runtime-license.txt");
        var modelLicense = Path.Combine(root, "model-license.txt");
        await File.WriteAllTextAsync(model, "fixture model bytes");
        await File.WriteAllTextAsync(runtimeLicense, "fixture runtime license");
        await File.WriteAllTextAsync(modelLicense, "fixture model license");
        var executable = FixtureExecutable();
        var binding = new ApprovedWhisperBackendBinding(
            executable,
            await WhisperCliTranscriber.ComputeSha256Async(executable),
            model,
            await WhisperCliTranscriber.ComputeSha256Async(model),
            runtimeLicense,
            await WhisperCliTranscriber.ComputeSha256Async(runtimeLicense),
            modelLicense,
            await WhisperCliTranscriber.ComputeSha256Async(modelLicense),
            CapturedAt.AddMinutes(-2));
        var backend = new WhisperTranscriptionBenchmarkBackend(binding, TimeSpan.FromSeconds(5));
        var fixtures = new[]
        {
            new TranscriptionBenchmarkFixture("production-adapter", wave, "DCx transcription fixture 42."),
        };
        var limits = new TranscriptionAccuracyLimits(
            0,
            0,
            new LocalBenchmarkResourceLimits(TimeSpan.FromSeconds(5), 1024L * 1024 * 1024));
        var approval = await TranscriptionApprovalAsync(backend.BindingSha256, fixtures, limits);

        var evidence = await LocalModelBenchmarkRunner.RunTranscriptionAtAsync(
            approval, true, fixtures, limits, backend, CapturedAt);

        Assert.True(evidence.Result.Passed);
        Assert.True(evidence.Result.Resources.PeakWorkingSetBytes is > 0);
        Assert.True(evidence.Result.Resources.UnloadConfirmed);
    }

    [Fact]
    public async Task EmbeddingRunner_BatchesSequentiallyAndReturnsAggregateOnlyEvidence()
    {
        var documents = Enumerable.Range(0, 33)
            .Select(index => new EmbeddingBenchmarkDocument(
                $"doc-{index:D2}",
                index == 0 ? "relevant document text" : $"noise document {index:D2}"))
            .ToArray();
        var queries = new[]
        {
            new EmbeddingBenchmarkQuery("query-00", "relevant query text", ["doc-00"]),
        };
        var limits = EmbeddingLimits();
        var binding = Binding(LocalModelBenchmarkKind.EmbeddingRetrieval);
        var approval = EmbeddingApproval(binding, documents, queries, limits);
        var batchSizes = new List<int>();
        var backend = new FakeEmbeddingBackend(binding, (inputs, _) =>
        {
            batchSizes.Add(inputs.Count);
            var vectors = inputs
                .Select(text => text.StartsWith("relevant", StringComparison.Ordinal)
                    ? new[] { 1f, 0f }
                    : new[] { 0f, 1f })
                .ToArray();
            return Task.FromResult(new EmbeddingBenchmarkBackendResult(vectors, Resources()));
        });

        var evidence = await LocalModelBenchmarkRunner.RunEmbeddingAtAsync(
            approval, true, documents, queries, limits, backend, CapturedAt);

        Assert.True(evidence.Result.Passed);
        Assert.Equal([32, 2], batchSizes);
        Assert.Equal(500, evidence.Result.Resources.ElapsedMilliseconds);
        Assert.Equal(1, evidence.Result.RelevantDocumentsRetrieved);
        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("relevant document text", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("doc-00", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Vector", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddingRunner_DeniesChangedSuiteWrongCountAndBindingMutation()
    {
        var documents = new[]
        {
            new EmbeddingBenchmarkDocument("doc-a", "alpha document"),
            new EmbeddingBenchmarkDocument("doc-b", "beta document"),
        };
        var queries = new[] { new EmbeddingBenchmarkQuery("query", "alpha query", ["doc-a"]) };
        var limits = EmbeddingLimits();
        var binding = Binding(LocalModelBenchmarkKind.EmbeddingRetrieval);
        var approval = EmbeddingApproval(binding, documents, queries, limits);
        var wrongCount = new FakeEmbeddingBackend(binding, (_, _) => Task.FromResult(
            new EmbeddingBenchmarkBackendResult([[1f, 0f]], Resources())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunEmbeddingAtAsync(
            approval,
            true,
            documents.Select(item => item.Id == "doc-a" ? item with { Text = "changed" } : item).ToArray(),
            queries,
            limits,
            wrongCount,
            CapturedAt));
        Assert.Equal(0, wrongCount.CallCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunEmbeddingAtAsync(
            approval, true, documents, queries, limits, wrongCount, CapturedAt));

        FakeEmbeddingBackend? mutable = null;
        mutable = new FakeEmbeddingBackend(binding, (inputs, _) =>
        {
            mutable!.BindingSha256 = Digest('8');
            return Task.FromResult(new EmbeddingBenchmarkBackendResult(
                inputs.Select(_ => new[] { 1f, 0f }).ToArray(),
                Resources()));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelBenchmarkRunner.RunEmbeddingAtAsync(
            approval, true, documents, queries, limits, mutable, CapturedAt));
    }

    [Fact]
    public void DigestPreparation_IsOrderStableAndRejectsAmbiguousInputs()
    {
        var forward = new[]
        {
            new LocalModelBenchmarkIdentityPart("model", Digest('1')),
            new LocalModelBenchmarkIdentityPart("runtime", Digest('2')),
        };
        var reverse = forward.Reverse().ToArray();

        Assert.Equal(
            LocalModelBenchmarkRunner.ComputeBackendBindingSha256(LocalModelBenchmarkKind.Transcription, forward),
            LocalModelBenchmarkRunner.ComputeBackendBindingSha256(LocalModelBenchmarkKind.Transcription, reverse));
        Assert.Throws<ArgumentException>(() => LocalModelBenchmarkRunner.ComputeBackendBindingSha256(
            LocalModelBenchmarkKind.Transcription,
            [forward[0], forward[0]]));
        Assert.Throws<ArgumentException>(() => LocalModelBenchmarkRunner.ComputeEmbeddingSuiteSha256(
            [new("duplicate", "one"), new("duplicate", "two")],
            [new("query", "text", ["duplicate"])]));
        Assert.Throws<ArgumentException>(() => LocalModelBenchmarkRunner.ComputeEmbeddingSuiteSha256(
            [new("doc", "one")],
            [new("query", "text", ["unknown"])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalModelBenchmarkRunner.ComputeEmbeddingPolicySha256(
            EmbeddingLimits() with { MinimumRecallAtK = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalModelBenchmarkRunner.ComputeEmbeddingPolicySha256(
            EmbeddingLimits() with { Resources = ResourceLimits(TimeSpan.FromSeconds(1)) with { RequireUnloadConfirmation = false } }));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<string> CreateWaveAsync(string name)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, WaveBytes());
        return path;
    }

    private static async Task<LocalModelBenchmarkApproval> TranscriptionApprovalAsync(
        string binding,
        IReadOnlyList<TranscriptionBenchmarkFixture> fixtures,
        TranscriptionAccuracyLimits limits) =>
        Approval(
            LocalModelBenchmarkKind.Transcription,
            binding,
            await LocalModelBenchmarkRunner.ComputeTranscriptionSuiteSha256Async(fixtures),
            LocalModelBenchmarkRunner.ComputeTranscriptionPolicySha256(limits));

    private static LocalModelBenchmarkApproval EmbeddingApproval(
        string binding,
        IReadOnlyList<EmbeddingBenchmarkDocument> documents,
        IReadOnlyList<EmbeddingBenchmarkQuery> queries,
        EmbeddingRetrievalLimits limits) =>
        Approval(
            LocalModelBenchmarkKind.EmbeddingRetrieval,
            binding,
            LocalModelBenchmarkRunner.ComputeEmbeddingSuiteSha256(documents, queries),
            LocalModelBenchmarkRunner.ComputeEmbeddingPolicySha256(limits));

    private static LocalModelBenchmarkApproval Approval(
        LocalModelBenchmarkKind kind,
        string binding,
        string suite,
        string policy) =>
        new(kind, binding, suite, policy, CapturedAt.AddMinutes(-1), CapturedAt.AddMinutes(10));

    private static string Binding(LocalModelBenchmarkKind kind) =>
        LocalModelBenchmarkRunner.ComputeBackendBindingSha256(
            kind,
            [
                new("model", Digest('1')),
                new("model_license", Digest('2')),
                new("runtime", Digest('3')),
                new("runtime_license", Digest('4')),
            ]);

    private static TranscriptionAccuracyLimits TranscriptionLimits() =>
        new(0, 0, ResourceLimits(TimeSpan.FromSeconds(2)));

    private static EmbeddingRetrievalLimits EmbeddingLimits() =>
        new(1, 1, 1, ResourceLimits(TimeSpan.FromSeconds(2)));

    private static LocalBenchmarkResourceLimits ResourceLimits(TimeSpan elapsed) =>
        new(elapsed, 128 * 1024 * 1024);

    private static LocalBenchmarkResourceObservation Resources() =>
        new(TimeSpan.FromMilliseconds(250), 64 * 1024 * 1024, null, true);

    private static string Digest(char character) => new(character, 64);

    private static byte[] WaveBytes(bool extraByte = false) => extraByte
        ?
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 5, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E', 0,
        ]
        :
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
        ];

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(
            directory.FullName,
            "tests",
            "DCx.FakeWhisperCli",
            "bin",
            "Release",
            "net10.0",
            "DCx.FakeWhisperCli.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException("Build the fake Whisper CLI fixture before running tests.", executable);
    }

    private sealed class FakeTranscriptionBackend(
        string bindingSha256,
        Func<string, CancellationToken, Task<TranscriptionBenchmarkBackendResult>> handler)
        : ITranscriptionBenchmarkBackend
    {
        public string BindingSha256 { get; set; } = bindingSha256;
        public int CallCount { get; private set; }

        public Task<TranscriptionBenchmarkBackendResult> TranscribeAsync(
            string wavePath,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(wavePath, cancellationToken);
        }
    }

    private sealed class FakeEmbeddingBackend(
        string bindingSha256,
        Func<IReadOnlyList<string>, CancellationToken, Task<EmbeddingBenchmarkBackendResult>> handler)
        : IEmbeddingBenchmarkBackend
    {
        public string BindingSha256 { get; set; } = bindingSha256;
        public int CallCount { get; private set; }

        public Task<EmbeddingBenchmarkBackendResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(inputs, cancellationToken);
        }
    }
}
