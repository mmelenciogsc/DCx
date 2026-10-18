using System.Net;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class OllamaLocalClientTests
{
    private const string VisionDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EmbeddingDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherDigest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string VisionModel = "fixture-vlm:" + VisionDigest;
    private const string EmbeddingModel = "fixture-embed:" + EmbeddingDigest;
    private static readonly string LicenseDigest = Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes("Apache-2.0")));

    [Fact]
    public void Constructor_RejectsNonLoopbackEndpoint()
    {
        Assert.Throws<ArgumentException>(() => new OllamaLocalClient(new Uri("https://example.com/")));
        Assert.Throws<ArgumentException>(() => new OllamaLocalClient(new Uri("http://localhost:11434/api/")));
    }

    [Fact]
    public void ProductionTransport_DisablesRedirectsCookiesAndProxy()
    {
        using var handler = OllamaLocalClient.CreateHardenedHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Fact]
    public async Task ResponseOriginEscape_IsRejectedBeforeContentIsUsed()
    {
        var handler = new FixtureHandler { ResponseUri = new Uri("http://127.0.0.1:6553/api/tags") };
        using var client = new OllamaLocalClient(handler, new Uri("http://127.0.0.1:11434/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync());
    }

    [Fact]
    public async Task InventoryAndExpectedDigest_RejectMalformedIdentityData()
    {
        using var malformed = new OllamaLocalClient(
            new FixtureHandler { ReturnMalformedInventory = true },
            new Uri("http://localhost:11434/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.ListModelsAsync());

        using var client = new OllamaLocalClient(
            new FixtureHandler(),
            new Uri("http://localhost:11434/"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DescribeImagePinnedAsync(
                VisionModel, "not-a-digest", LicenseDigest, "missing.png", "Describe."));
    }

    [Fact]
    public async Task ListAndVision_UseExpectedLocalContracts()
    {
        var handler = new FixtureHandler();
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(image, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        try
        {
            var models = await client.ListModelsAsync();
            var result = await client.DescribeImagePinnedAsync(
                VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes.");

            var vision = Assert.Single(models, candidate => candidate.Name == VisionModel);
            Assert.True(vision.SupportsVision);
            Assert.Equal(VisionDigest, vision.Digest);
            Assert.True(vision.HasDigestBoundName);
            Assert.Equal("A blue square. A red circle.", result.Text);
            Assert.Equal(12, result.PromptTokens);
            Assert.Equal(8, result.OutputTokens);
            Assert.Contains("\"stream\":false", handler.ChatRequest, StringComparison.Ordinal);
            Assert.Contains("\"images\":[\"iVBORw0KGgo=\"]", handler.ChatRequest, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task VisionInference_IsSerializedAcrossClientsForTheEntirePinnedOperation()
    {
        var coordinationRoot = Path.Combine(
            Path.GetTempPath(),
            $"dcx-ollama-client-lease-{Guid.NewGuid():N}");
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(
            image,
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var probe = new ConcurrencyProbe();
        try
        {
            var coordinator = new OllamaInferenceLeaseCoordinator(
                coordinationRoot,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10));
            using var first = new OllamaLocalClient(
                new FixtureHandler { VisionProbe = probe },
                new Uri("http://localhost:11434/"),
                TimeSpan.FromSeconds(2),
                coordinator);
            using var second = new OllamaLocalClient(
                new FixtureHandler { VisionProbe = probe },
                new Uri("http://localhost:11434/"),
                TimeSpan.FromSeconds(2),
                coordinator);

            await Task.WhenAll(
                first.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."),
                second.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));

            Assert.Equal(1, probe.MaximumConcurrent);
            Assert.Equal(2, probe.Completed);
            Assert.Equal(
                0,
                new FileInfo(Path.Combine(
                    coordinationRoot,
                    OllamaInferenceLeaseCoordinator.LockFileName)).Length);
        }
        finally
        {
            File.Delete(image);
            if (Directory.Exists(coordinationRoot)) Directory.Delete(coordinationRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InspectionAndEmbedding_ValidateCapabilitiesAndBoundedVectors()
    {
        var coordinator = new TrackingLeaseCoordinator();
        var handler = new FixtureHandler { LeaseIsActive = () => coordinator.IsActive };
        using var client = new OllamaLocalClient(
            handler,
            new Uri("http://127.0.0.1:11434/"),
            timeout: null,
            inferenceLeaseCoordinator: coordinator);

        var inspection = await client.InspectModelAsync(EmbeddingModel);
        handler.RequireInferenceLease = true;
        var result = await client.EmbedPinnedAsync(
            EmbeddingModel, EmbeddingDigest, LicenseDigest, ["first", "second"]);

        Assert.Contains("embedding", inspection.Capabilities);
        Assert.Equal("Apache-2.0", inspection.License);
        Assert.Equal(2, result.Embeddings.Count);
        Assert.All(result.Embeddings, vector => Assert.Equal(3, vector.Length));
        Assert.Equal(7, result.PromptTokens);
        Assert.Contains("\"truncate\":false", handler.EmbeddingRequest, StringComparison.Ordinal);
        Assert.Contains("\"keep_alive\":0", handler.EmbeddingRequest, StringComparison.Ordinal);
        Assert.Equal(1, coordinator.Acquisitions);
        Assert.False(coordinator.IsActive);
        Assert.False(handler.InferenceLeaseViolation);
    }

    [Fact]
    public async Task Embedding_RejectsInvalidInputAndInconsistentResponseDimensions()
    {
        var handler = new FixtureHandler { ReturnInconsistentEmbeddings = true };
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.EmbedPinnedAsync(EmbeddingModel, EmbeddingDigest, LicenseDigest, []));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.EmbedPinnedAsync(
                EmbeddingModel, EmbeddingDigest, LicenseDigest, [new string('x', 8193)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedPinnedAsync(
                EmbeddingModel, EmbeddingDigest, LicenseDigest, ["one", "two"]));
    }

    [Fact]
    public async Task Vision_RejectsUnsupportedImageDataBeforeSending()
    {
        var handler = new FixtureHandler();
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(image, "not an image"u8.ToArray());
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));
            Assert.Empty(handler.ChatRequest);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task Vision_RejectsMismatchedOrIncompleteResponse()
    {
        var handler = new FixtureHandler { ReturnIncompleteVision = true };
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(image, [0xFF, 0xD8, 0xFF]);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task PinnedInference_RejectsDuplicateOrChangedRuntimeBinding()
    {
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(
            image, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        try
        {
            using var duplicate = new OllamaLocalClient(
                new FixtureHandler { DuplicateVisionBinding = true },
                new Uri("http://localhost:11434/"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                duplicate.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));

            using var changed = new OllamaLocalClient(
                new FixtureHandler { ChangeVisionBindingAfterInference = true },
                new Uri("http://localhost:11434/"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                changed.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));

            using var changedLicense = new OllamaLocalClient(
                new FixtureHandler { ChangeLicenseAfterInference = true },
                new Uri("http://localhost:11434/"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                changedLicense.DescribeImagePinnedAsync(
                    VisionModel, VisionDigest, LicenseDigest, image, "Describe shapes."));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task EmbeddingBenchmark_BindsRuntimeMeasuresLoadedModelAndConfirmsUnload()
    {
        var coordinator = new TrackingLeaseCoordinator();
        var handler = new FixtureHandler { LeaseIsActive = () => coordinator.IsActive };
        using var client = new OllamaLocalClient(
            handler,
            new Uri("http://localhost:11434/"),
            timeout: null,
            inferenceLeaseCoordinator: coordinator);
        var binding = new ApprovedLocalModelBinding(
            EmbeddingModel,
            EmbeddingDigest,
            LicenseDigest,
            new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero));
        using var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client);

        handler.RequireInferenceLease = true;
        var result = await backend.EmbedAsync(["first", "second"], CancellationToken.None);

        Assert.Equal("0.40.0", backend.RuntimeVersion);
        Assert.Equal(64, backend.BindingSha256.Length);
        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(400_000_000, result.Resources.PeakWorkingSetBytes);
        Assert.Equal(300_000_000, result.Resources.PeakGpuBytes);
        Assert.True(result.Resources.UnloadConfirmed);
        Assert.True(result.Resources.Elapsed > TimeSpan.Zero);
        Assert.Contains("\"keep_alive\":\"1m\"", handler.EmbeddingRequest, StringComparison.Ordinal);
        Assert.Contains("\"keep_alive\":0", handler.UnloadRequest, StringComparison.Ordinal);
        Assert.True(handler.UnloadRequested);
        Assert.False(handler.Loaded);
        Assert.True(handler.RunningModelRequests >= 5);
        Assert.Equal(1, coordinator.Acquisitions);
        Assert.False(coordinator.IsActive);
        Assert.False(handler.InferenceLeaseViolation);
    }

    [Fact]
    public async Task RuntimeAndRunningModelInventory_RejectMalformedResourceEvidence()
    {
        using (var invalidVersion = new OllamaLocalClient(
                   new FixtureHandler { ReturnInvalidRuntimeVersion = true },
                   new Uri("http://localhost:11434/")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalidVersion.GetVersionAsync());
        }

        using var invalidResources = new OllamaLocalClient(
            new FixtureHandler { InitiallyLoaded = true, ReturnInvalidRunningResources = true },
            new Uri("http://localhost:11434/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidResources.ListRunningModelsAsync());
    }

    [Fact]
    public async Task EmbeddingBenchmark_DeniesPreloadedChangedRuntimeAndUnconfirmedUnload()
    {
        var binding = new ApprovedLocalModelBinding(
            EmbeddingModel,
            EmbeddingDigest,
            LicenseDigest,
            new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero));

        var preloadedHandler = new FixtureHandler { InitiallyLoaded = true };
        using (var client = new OllamaLocalClient(preloadedHandler, new Uri("http://localhost:11434/")))
        using (var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                backend.EmbedAsync(["first", "second"], CancellationToken.None));
            Assert.False(preloadedHandler.UnloadRequested);
        }

        var changedHandler = new FixtureHandler { ChangeRuntimeAfterEmbedding = true };
        using (var client = new OllamaLocalClient(changedHandler, new Uri("http://localhost:11434/")))
        using (var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                backend.EmbedAsync(["first", "second"], CancellationToken.None));
            Assert.True(changedHandler.UnloadRequested);
            Assert.False(changedHandler.Loaded);
        }

        var unloadHandler = new FixtureHandler { RefuseUnload = true };
        using (var client = new OllamaLocalClient(unloadHandler, new Uri("http://localhost:11434/")))
        using (var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                backend.EmbedAsync(["first", "second"], CancellationToken.None));
            Assert.True(unloadHandler.UnloadRequested);
            Assert.True(unloadHandler.Loaded);
        }
    }

    [Fact]
    public async Task EmbeddingBenchmark_CancellationStillAttemptsBoundedUnload()
    {
        var handler = new FixtureHandler { DelayEmbedding = true };
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));
        var binding = new ApprovedLocalModelBinding(
            EmbeddingModel,
            EmbeddingDigest,
            LicenseDigest,
            new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero));
        using var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.EmbedAsync(["first", "second"], cancellation.Token));

        Assert.True(handler.UnloadRequested);
        Assert.False(handler.Loaded);
    }

    [Fact]
    public async Task EmbeddingBenchmark_ComposesPinnedOllamaAdapterWithAggregateRunner()
    {
        var handler = new FixtureHandler();
        using var client = new OllamaLocalClient(handler, new Uri("http://localhost:11434/"));
        var binding = new ApprovedLocalModelBinding(
            EmbeddingModel,
            EmbeddingDigest,
            LicenseDigest,
            new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero));
        using var backend = await OllamaEmbeddingBenchmarkBackend.CreateAsync(binding, client);
        var documents = new[] { new EmbeddingBenchmarkDocument("private-document", "private corpus text") };
        var queries = new[]
        {
            new EmbeddingBenchmarkQuery("private-query", "private query text", ["private-document"]),
        };
        var limits = new EmbeddingRetrievalLimits(
            1,
            1,
            1,
            new LocalBenchmarkResourceLimits(
                TimeSpan.FromSeconds(5),
                500_000_000,
                400_000_000));
        var reviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var approval = new LocalModelBenchmarkApproval(
            LocalModelBenchmarkKind.EmbeddingRetrieval,
            backend.BindingSha256,
            LocalModelBenchmarkRunner.ComputeEmbeddingSuiteSha256(documents, queries),
            LocalModelBenchmarkRunner.ComputeEmbeddingPolicySha256(limits),
            reviewedAt,
            reviewedAt.AddMinutes(10));

        var evidence = await LocalModelBenchmarkRunner.RunEmbeddingAsync(
            approval, true, documents, queries, limits, backend);

        Assert.True(evidence.Result.Passed);
        Assert.Equal(400_000_000, evidence.Result.Resources.PeakWorkingSetBytes);
        Assert.Equal(300_000_000, evidence.Result.Resources.PeakGpuBytes);
        var json = System.Text.Json.JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("private corpus", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-document", json, StringComparison.Ordinal);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string ChatRequest { get; private set; } = string.Empty;
        public string EmbeddingRequest { get; private set; } = string.Empty;
        public string UnloadRequest { get; private set; } = string.Empty;
        public bool ReturnInconsistentEmbeddings { get; init; }
        public bool ReturnIncompleteVision { get; init; }
        public bool DuplicateVisionBinding { get; init; }
        public bool ChangeVisionBindingAfterInference { get; init; }
        public bool ChangeLicenseAfterInference { get; init; }
        public bool ReturnMalformedInventory { get; init; }
        public bool InitiallyLoaded
        {
            get => initiallyLoaded;
            init
            {
                initiallyLoaded = value;
                Loaded = value;
            }
        }
        public bool ChangeRuntimeAfterEmbedding { get; init; }
        public bool RefuseUnload { get; init; }
        public bool DelayEmbedding { get; init; }
        public ConcurrencyProbe? VisionProbe { get; init; }
        public Func<bool>? LeaseIsActive { get; init; }
        public bool RequireInferenceLease { get; set; }
        public bool InferenceLeaseViolation { get; private set; }
        public bool ReturnInvalidRuntimeVersion { get; init; }
        public bool ReturnInvalidRunningResources { get; init; }
        public Uri? ResponseUri { get; init; }
        public bool Loaded { get; private set; }
        public bool UnloadRequested { get; private set; }
        public int RunningModelRequests { get; private set; }
        private int inventoryCalls;
        private int inspectionCalls;
        private bool embedded;
        private bool initiallyLoaded;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequireInferenceLease && (LeaseIsActive is null || !LeaseIsActive()))
            {
                InferenceLeaseViolation = true;
            }

            if (request.RequestUri?.AbsolutePath == "/api/version")
            {
                var version = ReturnInvalidRuntimeVersion
                    ? "0.40.0 invalid"
                    : ChangeRuntimeAfterEmbedding && embedded ? "0.41.0" : "0.40.0";
                return Json($"{{\"version\":\"{version}\"}}", request);
            }

            if (request.RequestUri?.AbsolutePath == "/api/ps")
            {
                RunningModelRequests++;
                return Json(Loaded
                    ? $"{{\"models\":[{{\"name\":\"{EmbeddingModel}\",\"digest\":\"{EmbeddingDigest}\",\"size\":400000000,\"size_vram\":{(ReturnInvalidRunningResources ? 500000000 : 300000000)},\"expires_at\":\"2026-10-18T04:00:00Z\"}}]}}"
                    : "{\"models\":[]}", request);
            }

            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                if (ReturnMalformedInventory)
                {
                    return Json(
                        "{\"models\":[{\"name\":\"mutable-tag\",\"size\":1000,\"details\":{}}]}",
                        request);
                }

                inventoryCalls++;
                var visionDigest = ChangeVisionBindingAfterInference && inventoryCalls > 1
                    ? OtherDigest
                    : VisionDigest;
                var duplicate = DuplicateVisionBinding
                    ? $",{{\"name\":\"{VisionModel}\",\"digest\":\"{OtherDigest}\",\"size\":1001,\"capabilities\":[\"completion\",\"vision\"],\"details\":{{\"parameter_size\":\"1B\",\"quantization_level\":\"Q4\"}}}}"
                    : string.Empty;
                return Json(
                    $"{{\"models\":[{{\"name\":\"{VisionModel}\",\"digest\":\"{visionDigest}\",\"size\":1000,\"capabilities\":[\"completion\",\"vision\"],\"details\":{{\"parameter_size\":\"1B\",\"quantization_level\":\"Q4\"}}}}{duplicate},{{\"name\":\"{EmbeddingModel}\",\"digest\":\"{EmbeddingDigest}\",\"size\":500,\"capabilities\":[\"embedding\"],\"details\":{{\"parameter_size\":\"300M\",\"quantization_level\":\"F16\"}}}}]}}",
                    request);
            }

            if (request.RequestUri?.AbsolutePath == "/api/show")
            {
                inspectionCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var license = ChangeLicenseAfterInference && inspectionCalls > 1
                    ? "Changed-License"
                    : "Apache-2.0";
                return Json(body.Contains(VisionModel, StringComparison.Ordinal)
                    ? $"{{\"license\":\"{license}\",\"capabilities\":[\"completion\",\"vision\"],\"details\":{{\"family\":\"vlm\",\"parameter_size\":\"1B\",\"quantization_level\":\"Q4\"}}}}"
                    : "{\"license\":\"Apache-2.0\",\"capabilities\":[\"embedding\"],\"details\":{\"family\":\"bert\",\"parameter_size\":\"300M\",\"quantization_level\":\"F16\"}}", request);
            }

            if (request.RequestUri?.AbsolutePath == "/api/embed")
            {
                EmbeddingRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                embedded = true;
                if (EmbeddingRequest.Contains("\"keep_alive\":\"1m\"", StringComparison.Ordinal)) Loaded = true;
                if (DelayEmbedding) await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return Json(ReturnInconsistentEmbeddings
                    ? $"{{\"model\":\"{EmbeddingModel}\",\"embeddings\":[[0.1,0.2],[0.3]],\"prompt_eval_count\":7,\"total_duration\":100}}"
                    : $"{{\"model\":\"{EmbeddingModel}\",\"embeddings\":[[0.1,0.2,0.3],[0.4,0.5,0.6]],\"prompt_eval_count\":7,\"total_duration\":100,\"load_duration\":20}}", request);
            }

            if (request.RequestUri?.AbsolutePath == "/api/generate")
            {
                UnloadRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                UnloadRequested = true;
                if (!RefuseUnload) Loaded = false;
                return Json(RefuseUnload
                    ? $"{{\"model\":\"{EmbeddingModel}\",\"response\":\"\",\"done\":true,\"done_reason\":\"stop\"}}"
                    : $"{{\"model\":\"{EmbeddingModel}\",\"response\":\"\",\"done\":true,\"done_reason\":\"unload\"}}", request);
            }

            ChatRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (VisionProbe is not null)
            {
                await VisionProbe.ObserveAsync(cancellationToken);
            }
            return Json(ReturnIncompleteVision
                ? "{\"model\":\"wrong-model\",\"done\":false,\"message\":{\"content\":\"Partial\"}}"
                : $"{{\"model\":\"{VisionModel}\",\"done\":true,\"message\":{{\"content\":\"A blue square. A red circle.\"}},\"prompt_eval_count\":12,\"eval_count\":8,\"total_duration\":1000}}", request);
        }

        private HttpResponseMessage Json(string value, HttpRequestMessage request) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
            RequestMessage = ResponseUri is null ? request : new HttpRequestMessage(request.Method, ResponseUri),
        };
    }

    private sealed class ConcurrencyProbe
    {
        private int active;
        private int maximumConcurrent;
        private int completed;

        public int MaximumConcurrent => Volatile.Read(ref maximumConcurrent);
        public int Completed => Volatile.Read(ref completed);

        public async Task ObserveAsync(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var maximum = Volatile.Read(ref maximumConcurrent);
                if (current <= maximum ||
                    Interlocked.CompareExchange(ref maximumConcurrent, current, maximum) == maximum)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                Interlocked.Increment(ref completed);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class TrackingLeaseCoordinator : IOllamaInferenceLeaseCoordinator
    {
        private int active;
        private int acquisitions;

        public bool IsActive => Volatile.Read(ref active) == 1;
        public int Acquisitions => Volatile.Read(ref acquisitions);

        public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            {
                throw new InvalidOperationException("The test lease was acquired concurrently.");
            }
            Interlocked.Increment(ref acquisitions);
            return Task.FromResult<IDisposable>(new Release(this));
        }

        private sealed class Release(TrackingLeaseCoordinator owner) : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Assert.Equal(1, Interlocked.Exchange(ref owner.active, 0));
            }
        }
    }
}
