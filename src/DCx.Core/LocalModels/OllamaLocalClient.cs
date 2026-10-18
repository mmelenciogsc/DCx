using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCx.Core.LocalModels;

public sealed class OllamaLocalClient : IDisposable
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private const int MaximumModelNameCharacters = 256;
    private const int MaximumPromptCharacters = 8192;
    private const int MaximumEmbeddingInputs = 32;
    private const int MaximumEmbeddingInputCharacters = 8192;
    private const int MaximumEmbeddingDimensions = 8192;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumVisionResponseCharacters = 32 * 1024;
    private const int RequiredQuiescentObservations = 3;
    private static readonly TimeSpan QuiescentObservationInterval = TimeSpan.FromMilliseconds(250);
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly IOllamaInferenceLeaseCoordinator inferenceLeaseCoordinator;
    private readonly ILocalModelResourceAdmission resourceAdmission;

    public OllamaLocalClient(Uri baseAddress, TimeSpan? timeout = null)
        : this(
            OllamaInferenceLeaseCoordinator.CreateForCurrentUser(),
            WindowsLocalModelResourceAdmission.CreateForCurrentUser(),
            new HttpClient(CreateHardenedHandler(), disposeHandler: true),
            baseAddress,
            timeout)
    {
    }

    private OllamaLocalClient(
        IOllamaInferenceLeaseCoordinator inferenceLeaseCoordinator,
        ILocalModelResourceAdmission resourceAdmission,
        HttpClient httpClient,
        Uri baseAddress,
        TimeSpan? timeout)
        : this(httpClient, baseAddress, timeout, inferenceLeaseCoordinator, resourceAdmission)
    {
    }

    internal OllamaLocalClient(HttpMessageHandler handler, Uri baseAddress, TimeSpan? timeout = null)
        : this(
            new HttpClient(handler, disposeHandler: true),
            baseAddress,
            timeout,
            UncoordinatedOllamaInferenceLease.Instance,
            UncheckedLocalModelResourceAdmission.Instance)
    {
    }

    internal OllamaLocalClient(
        HttpMessageHandler handler,
        Uri baseAddress,
        TimeSpan? timeout,
        IOllamaInferenceLeaseCoordinator inferenceLeaseCoordinator)
        : this(
            new HttpClient(handler, disposeHandler: true),
            baseAddress,
            timeout,
            inferenceLeaseCoordinator,
            UncheckedLocalModelResourceAdmission.Instance)
    {
    }

    internal OllamaLocalClient(
        HttpMessageHandler handler,
        Uri baseAddress,
        TimeSpan? timeout,
        IOllamaInferenceLeaseCoordinator inferenceLeaseCoordinator,
        ILocalModelResourceAdmission resourceAdmission)
        : this(
            new HttpClient(handler, disposeHandler: true),
            baseAddress,
            timeout,
            inferenceLeaseCoordinator,
            resourceAdmission)
    {
    }

    private OllamaLocalClient(
        HttpClient httpClient,
        Uri baseAddress,
        TimeSpan? timeout,
        IOllamaInferenceLeaseCoordinator inferenceLeaseCoordinator,
        ILocalModelResourceAdmission resourceAdmission)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (inferenceLeaseCoordinator is null)
        {
            _httpClient.Dispose();
            throw new ArgumentNullException(nameof(inferenceLeaseCoordinator));
        }
        this.inferenceLeaseCoordinator = inferenceLeaseCoordinator;
        if (resourceAdmission is null)
        {
            _httpClient.Dispose();
            throw new ArgumentNullException(nameof(resourceAdmission));
        }
        this.resourceAdmission = resourceAdmission;
        if (baseAddress is null)
        {
            _httpClient.Dispose();
            throw new ArgumentNullException(nameof(baseAddress));
        }
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttp || !baseAddress.IsLoopback ||
            baseAddress.AbsolutePath != "/" || !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) || !string.IsNullOrEmpty(baseAddress.UserInfo))
        {
            _httpClient.Dispose();
            throw new ArgumentException("DCx permits the Ollama adapter only at a loopback HTTP origin.", nameof(baseAddress));
        }

        var boundedTimeout = timeout ?? TimeSpan.FromMinutes(3);
        if (boundedTimeout < TimeSpan.FromSeconds(1) || boundedTimeout > TimeSpan.FromMinutes(5))
        {
            _httpClient.Dispose();
            throw new ArgumentOutOfRangeException(nameof(timeout), "The Ollama timeout must be between one second and five minutes.");
        }

        _httpClient.BaseAddress = baseAddress;
        _httpClient.Timeout = boundedTimeout;
        _baseAddress = baseAddress;
    }

    internal static SocketsHttpHandler CreateHardenedHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.None,
    };

    public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/tags", cancellationToken);
        EnsureLoopbackResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var models = new List<LocalModelDescriptor>();
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            if (models.Count >= 1000) throw new InvalidOperationException("The Ollama model inventory exceeds its limit.");
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt64(out var size) || size <= 0 ||
                !item.TryGetProperty("digest", out var digestElement) ||
                digestElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("The Ollama model inventory contains an invalid entry.");
            }

            var name = nameElement.GetString()!;
            var digest = digestElement.GetString()!;
            ValidateModelName(name);
            ValidateDigest(digest, nameof(digest));
            if (!item.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The Ollama model inventory entry has no valid details object.");
            }

            var capabilities = new HashSet<string>(StringComparer.Ordinal);
            if (item.TryGetProperty("capabilities", out var capabilityElement))
            {
                if (capabilityElement.ValueKind != JsonValueKind.Array || capabilityElement.GetArrayLength() > 32)
                {
                    throw new InvalidOperationException("The Ollama capability inventory exceeds its limit.");
                }

                foreach (var capability in capabilityElement.EnumerateArray())
                {
                    if (capability.ValueKind != JsonValueKind.String ||
                        capability.GetString() is not { Length: > 0 and <= 64 } value ||
                        value.Any(char.IsControl))
                    {
                        throw new InvalidOperationException("The Ollama capability inventory contains an invalid value.");
                    }

                    capabilities.Add(value);
                }
            }
            models.Add(new LocalModelDescriptor(
                name,
                size,
                SupportsVision: capabilities.Contains("vision"),
                SupportsEmbeddings: capabilities.Contains("embedding"),
                details.TryGetProperty("parameter_size", out var parameterSize) ? parameterSize.GetString() : null,
                details.TryGetProperty("quantization_level", out var quantization) ? quantization.GetString() : null,
                Digest: digest.ToLowerInvariant()));
        }

        return models;
    }

    public async Task<OllamaModelInspection> InspectModelAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
        using var response = await _httpClient.PostAsJsonAsync(
            "api/show",
            new { model, verbose = false },
            cancellationToken).ConfigureAwait(false);
        EnsureLoopbackResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var capabilities = root.TryGetProperty("capabilities", out var values)
            ? values.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (capabilities.Count > 32) throw new InvalidOperationException("The Ollama capability inventory exceeds its limit.");
        var details = root.TryGetProperty("details", out var detailElement) ? detailElement : default;
        return new OllamaModelInspection(
            model,
            capabilities,
            root.TryGetProperty("license", out var license) ? license.GetString() : null,
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("family", out var family) ? family.GetString() : null,
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("parameter_size", out var parameters) ? parameters.GetString() : null,
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("quantization_level", out var quantization) ? quantization.GetString() : null);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/version", cancellationToken).ConfigureAwait(false);
        EnsureLoopbackResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            versionElement.GetString() is not { Length: > 0 and <= 64 } version ||
            version.Any(character => character > 0x7f || char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidOperationException("The Ollama runtime version response is invalid.");
        }
        return version;
    }

    public async Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/ps", cancellationToken).ConfigureAwait(false);
        EnsureLoopbackResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array || modelsElement.GetArrayLength() > 64)
        {
            throw new InvalidOperationException("The Ollama running-model response is invalid or exceeds its limit.");
        }

        var models = new List<OllamaRunningModel>(modelsElement.GetArrayLength());
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("digest", out var digestElement) || digestElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 ||
                !item.TryGetProperty("size_vram", out var vramElement) ||
                !vramElement.TryGetInt64(out var sizeVram) || sizeVram < 0 || sizeVram > size ||
                !item.TryGetProperty("expires_at", out var expiresElement) ||
                expiresElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    expiresElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var expiresAtUtc))
            {
                throw new InvalidOperationException("The Ollama running-model response contains an invalid entry.");
            }
            var name = nameElement.GetString()!;
            var digest = digestElement.GetString()!;
            ValidateModelName(name);
            ValidateDigest(digest, nameof(digest));
            var identity = name + "\n" + digest.ToLowerInvariant();
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException("The Ollama running-model response contains a duplicate identity.");
            }
            models.Add(new OllamaRunningModel(
                name,
                digest.ToLowerInvariant(),
                size,
                sizeVram,
                expiresAtUtc));
        }
        return models;
    }

    public async Task<OllamaEmbeddingResult> EmbedPinnedAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ValidateEmbeddingRequest(model, expectedDigest, expectedLicenseSha256, inputs, keepAlive: 0);
        using var inferenceLease = await inferenceLeaseCoordinator.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureRuntimeQuiescentAsync(cancellationToken).ConfigureAwait(false);
        return await EmbedPinnedCoreAsync(
            model, expectedDigest, expectedLicenseSha256, inputs, keepAlive: 0, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ValidatePinnedEmbeddingBindingAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        CancellationToken cancellationToken = default) =>
        RequireBoundModelAsync(
            model,
            expectedDigest,
            expectedLicenseSha256,
            requireVision: false,
            requireEmbedding: true,
            cancellationToken);

    public async Task<OllamaEmbeddingBenchmarkResult> EmbedPinnedForBenchmarkAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        string expectedRuntimeVersion,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ValidateRuntimeVersion(expectedRuntimeVersion);
        ValidateEmbeddingRequest(model, expectedDigest, expectedLicenseSha256, inputs, keepAlive: "1m");
        using var inferenceLease = await inferenceLeaseCoordinator.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                await GetVersionAsync(cancellationToken).ConfigureAwait(false),
                expectedRuntimeVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The reviewed Ollama runtime version changed.");
        }
        await EnsureRuntimeQuiescentAsync(cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        OllamaEmbeddingResult? result = null;
        OllamaRunningModel? loaded = null;
        try
        {
            result = await EmbedPinnedCoreAsync(
                model, expectedDigest, expectedLicenseSha256, inputs, keepAlive: "1m", cancellationToken)
                .ConfigureAwait(false);
            var matches = (await ListRunningModelsAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => string.Equals(item.Digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].Name, model, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Ollama did not expose one exact loaded model for resource measurement.");
            }
            loaded = matches[0];
        }
        finally
        {
            await UnloadAfterOperationAsync(model, expectedDigest).ConfigureAwait(false);
        }
        stopwatch.Stop();
        if (result is null || loaded is null ||
            !string.Equals(
                await GetVersionAsync(cancellationToken).ConfigureAwait(false),
                expectedRuntimeVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The reviewed Ollama runtime changed or returned no benchmark result.");
        }
        return new OllamaEmbeddingBenchmarkResult(
            result,
            new LocalBenchmarkResourceObservation(
                stopwatch.Elapsed,
                loaded.SizeBytes,
                loaded.SizeVramBytes > 0 ? loaded.SizeVramBytes : null,
                UnloadConfirmed: true));
    }

    public async Task<OllamaVisionResult> DescribeImagePinnedAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        string imagePath,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
        ValidateDigest(expectedDigest, nameof(expectedDigest));
        ValidateDigest(expectedLicenseSha256, nameof(expectedLicenseSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > MaximumPromptCharacters || prompt.Contains('\0'))
        {
            throw new ArgumentException("The vision prompt exceeds its safety bound.", nameof(prompt));
        }
        var file = new FileInfo(Path.GetFullPath(imagePath));
        if (!file.Exists || file.Length is <= 0 or > MaximumImageBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The image must exist and be between 1 byte and 20 MiB.", nameof(imagePath));
        }

        using var inferenceLease = await inferenceLeaseCoordinator.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureRuntimeQuiescentAsync(cancellationToken).ConfigureAwait(false);
        var imageBytes = new byte[checked((int)file.Length)];
        await using (var stream = new FileStream(
                         file.FullName,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(imageBytes, cancellationToken).ConfigureAwait(false);
        }

        string image;
        try
        {
            if (!HasSupportedImageSignature(imageBytes))
            {
                throw new ArgumentException("The image must be PNG, JPEG or WebP data.", nameof(imagePath));
            }

            var boundModel = await RequireBoundModelAsync(
                model, expectedDigest, expectedLicenseSha256,
                requireVision: true, requireEmbedding: false, cancellationToken).ConfigureAwait(false);
            resourceAdmission.EnsureVisionAllowed(boundModel);

            image = Convert.ToBase64String(imageBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(imageBytes);
        }

        try
        {
            var body = new
            {
                model,
                messages = new[] { new { role = "user", content = prompt, images = new[] { image } } },
                stream = false,
                keep_alive = 0,
                options = new { temperature = 0, num_predict = 160 },
            };
            using var response = await _httpClient.PostAsJsonAsync("api/chat", body, cancellationToken);
            EnsureLoopbackResponse(response);
            response.EnsureSuccessStatusCode();
            using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var responseModel) ||
                !string.Equals(responseModel.GetString(), model, StringComparison.Ordinal) ||
                !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("The Ollama vision response does not match the completed request.");
            }

            var text = content.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumVisionResponseCharacters)
            {
                throw new InvalidOperationException("The Ollama vision response text is empty or outside its limit.");
            }

            var result = new OllamaVisionResult(
                text,
                root.TryGetProperty("prompt_eval_count", out var promptTokens) ? promptTokens.GetInt32() : 0,
                root.TryGetProperty("eval_count", out var outputTokens) ? outputTokens.GetInt32() : 0,
                root.TryGetProperty("total_duration", out var duration) ? duration.GetInt64() : 0);
            await RequireBoundModelAsync(
                model, expectedDigest, expectedLicenseSha256,
                requireVision: true, requireEmbedding: false, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            await UnloadAfterOperationAsync(model, expectedDigest).ConfigureAwait(false);
        }
    }

    private async Task<OllamaEmbeddingResult> EmbedPinnedCoreAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        IReadOnlyList<string> inputs,
        object keepAlive,
        CancellationToken cancellationToken)
    {
        ValidateEmbeddingRequest(model, expectedDigest, expectedLicenseSha256, inputs, keepAlive);

        var boundModel = await RequireBoundModelAsync(
            model, expectedDigest, expectedLicenseSha256,
            requireVision: false, requireEmbedding: true, cancellationToken).ConfigureAwait(false);
        resourceAdmission.EnsureEmbeddingAllowed(boundModel);
        var shouldVerifyUnload = keepAlive is int keepAliveSeconds && keepAliveSeconds == 0;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/embed",
                new { model, input = inputs, truncate = false, keep_alive = keepAlive },
                cancellationToken).ConfigureAwait(false);
            EnsureLoopbackResponse(response);
            response.EnsureSuccessStatusCode();
            using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var responseModel) ||
                !string.Equals(responseModel.GetString(), model, StringComparison.Ordinal) ||
                !root.TryGetProperty("embeddings", out var embeddingsElement) ||
                embeddingsElement.ValueKind != JsonValueKind.Array || embeddingsElement.GetArrayLength() != inputs.Count)
            {
                throw new InvalidOperationException("The Ollama embedding response does not match the request.");
            }

            var embeddings = new List<float[]>(inputs.Count);
            int? dimensions = null;
            foreach (var embeddingElement in embeddingsElement.EnumerateArray())
            {
                if (embeddingElement.ValueKind != JsonValueKind.Array ||
                    embeddingElement.GetArrayLength() is <= 0 or > MaximumEmbeddingDimensions)
                {
                    throw new InvalidOperationException("An Ollama embedding has an invalid dimension count.");
                }
                dimensions ??= embeddingElement.GetArrayLength();
                if (embeddingElement.GetArrayLength() != dimensions)
                {
                    throw new InvalidOperationException("Ollama returned inconsistent embedding dimensions.");
                }
                var vector = embeddingElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                if (vector.Any(value => !float.IsFinite(value)))
                {
                    throw new InvalidOperationException("Ollama returned a non-finite embedding value.");
                }
                embeddings.Add(vector);
            }

            var result = new OllamaEmbeddingResult(
                model,
                embeddings,
                root.TryGetProperty("prompt_eval_count", out var promptTokens) ? promptTokens.GetInt32() : 0,
                root.TryGetProperty("total_duration", out var duration) ? duration.GetInt64() : 0,
                root.TryGetProperty("load_duration", out var loadDuration) ? loadDuration.GetInt64() : 0);
            if (result.PromptTokens < 0 || result.TotalDurationNanoseconds <= 0 || result.LoadDurationNanoseconds < 0)
            {
                throw new InvalidOperationException("The Ollama embedding response contains invalid resource counters.");
            }
            await RequireBoundModelAsync(
                model, expectedDigest, expectedLicenseSha256,
                requireVision: false, requireEmbedding: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (shouldVerifyUnload)
            {
                await UnloadAfterOperationAsync(model, expectedDigest).ConfigureAwait(false);
            }
        }
    }

    private async Task UnloadPinnedAsync(
        string model,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/generate",
            new { model, keep_alive = 0 },
            cancellationToken).ConfigureAwait(false);
        EnsureLoopbackResponse(response);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out var modelElement) ||
            !string.Equals(modelElement.GetString(), model, StringComparison.Ordinal) ||
            !root.TryGetProperty("done", out var doneElement) || doneElement.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("done_reason", out var reasonElement) ||
            !string.Equals(reasonElement.GetString(), "unload", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ollama did not confirm the exact model unload request.");
        }

        var consecutiveAbsent = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var remaining = await ListRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            if (remaining.Any(item =>
                    !string.Equals(item.Digest, expectedDigest, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "An unrelated Ollama model appeared during the operation. DCx did not unload it, and runtime quiescence could not be verified.");
            }
            if (!remaining.Any(item => string.Equals(item.Digest, expectedDigest, StringComparison.OrdinalIgnoreCase)))
            {
                consecutiveAbsent++;
                if (consecutiveAbsent >= RequiredQuiescentObservations) return;
            }
            else
            {
                consecutiveAbsent = 0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The exact Ollama model did not unload within its bounded window.");
    }

    private async Task EnsureRuntimeQuiescentAsync(CancellationToken cancellationToken)
    {
        for (var observation = 0; observation < RequiredQuiescentObservations; observation++)
        {
            if ((await ListRunningModelsAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
            {
                throw new InvalidOperationException(
                    "Ollama is already running a model. DCx will not interfere with another workload; retry after the local runtime is quiet.");
            }
            if (observation + 1 < RequiredQuiescentObservations)
            {
                await Task.Delay(QuiescentObservationInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task UnloadAfterOperationAsync(string model, string expectedDigest)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await UnloadPinnedAsync(model, expectedDigest, cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The exact Ollama model unload could not be verified within its cleanup deadline.");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static void ValidateModelName(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > MaximumModelNameCharacters || model.Contains('\0') || model.Any(char.IsControl))
        {
            throw new ArgumentException("The Ollama model name is invalid.", nameof(model));
        }
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 ||
            !digest.All(character => char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "An Ollama model binding requires a 64-character SHA-256 digest.", parameterName);
        }
    }

    private static void ValidateRuntimeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > 64 ||
            version.Any(character => character > 0x7f || char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The Ollama runtime version is invalid.", nameof(version));
        }
    }

    private static void ValidateEmbeddingRequest(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        IReadOnlyList<string> inputs,
        object keepAlive)
    {
        ValidateModelName(model);
        ValidateDigest(expectedDigest, nameof(expectedDigest));
        ValidateDigest(expectedLicenseSha256, nameof(expectedLicenseSha256));
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is 0 or > MaximumEmbeddingInputs || inputs.Any(input =>
                string.IsNullOrWhiteSpace(input) || input.Length > MaximumEmbeddingInputCharacters || input.Contains('\0')) ||
            keepAlive is not 0 && !string.Equals(keepAlive as string, "1m", StringComparison.Ordinal))
        {
            throw new ArgumentException("Embedding input or keep-alive policy is outside its safety bounds.", nameof(inputs));
        }
    }

    private async Task<LocalModelDescriptor> RequireBoundModelAsync(
        string model,
        string expectedDigest,
        string expectedLicenseSha256,
        bool requireVision,
        bool requireEmbedding,
        CancellationToken cancellationToken)
    {
        var matches = (await ListModelsAsync(cancellationToken).ConfigureAwait(false))
            .Where(candidate => string.Equals(candidate.Name, model, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            !string.Equals(matches[0].Digest, expectedDigest, StringComparison.OrdinalIgnoreCase) ||
            !matches[0].HasDigestBoundName ||
            requireVision && !matches[0].SupportsVision ||
            requireEmbedding && !matches[0].SupportsEmbeddings)
        {
            throw new InvalidOperationException(
                "The requested Ollama model is not uniquely bound to the expected digest and capability.");
        }

        var inspection = await InspectModelAsync(model, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(inspection.License) ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inspection.License))),
                expectedLicenseSha256,
                StringComparison.OrdinalIgnoreCase) ||
            requireVision && !inspection.Capabilities.Contains("vision") ||
            requireEmbedding && !inspection.Capabilities.Contains("embedding"))
        {
            throw new InvalidOperationException(
                "The requested Ollama model license or inspected capability changed after approval.");
        }

        return matches[0];
    }

    private void EnsureLoopbackResponse(HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri || responseUri.Scheme != Uri.UriSchemeHttp ||
            !responseUri.IsLoopback || !string.Equals(responseUri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            responseUri.Port != _baseAddress.Port)
        {
            throw new InvalidOperationException("The Ollama response escaped the approved loopback HTTP origin.");
        }
    }

    private static bool HasSupportedImageSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) ||
        bytes.StartsWith(JpegSignature) ||
        bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8);

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidOperationException("The Ollama response exceeds its size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidOperationException("The Ollama response exceeds its size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        output.Position = 0;
        return await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public sealed record OllamaVisionResult(string Text, int PromptTokens, int OutputTokens, long TotalDurationNanoseconds);

public sealed record OllamaModelInspection(
    string Name,
    IReadOnlySet<string> Capabilities,
    string? License,
    string? Family,
    string? ParameterSize,
    string? Quantization);

public sealed record OllamaEmbeddingResult(
    string Model,
    IReadOnlyList<float[]> Embeddings,
    int PromptTokens,
    long TotalDurationNanoseconds,
    long LoadDurationNanoseconds);

public sealed record OllamaRunningModel(
    string Name,
    string Digest,
    long SizeBytes,
    long SizeVramBytes,
    DateTimeOffset ExpiresAtUtc);

public sealed record OllamaEmbeddingBenchmarkResult(
    OllamaEmbeddingResult Embedding,
    LocalBenchmarkResourceObservation Resources);
