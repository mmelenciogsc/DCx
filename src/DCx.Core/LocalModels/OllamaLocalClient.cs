using System.Net;
using System.Net.Http.Json;
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
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;

    public OllamaLocalClient(Uri baseAddress, TimeSpan? timeout = null)
        : this(new HttpClient(CreateHardenedHandler(), disposeHandler: true), baseAddress, timeout)
    {
    }

    internal OllamaLocalClient(HttpMessageHandler handler, Uri baseAddress, TimeSpan? timeout = null)
        : this(new HttpClient(handler, disposeHandler: true), baseAddress, timeout)
    {
    }

    private OllamaLocalClient(HttpClient httpClient, Uri baseAddress, TimeSpan? timeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseAddress);
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

    public async Task<OllamaEmbeddingResult> EmbedPinnedAsync(
        string model,
        string expectedDigest,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
        ValidateDigest(expectedDigest, nameof(expectedDigest));
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is 0 or > MaximumEmbeddingInputs || inputs.Any(input =>
                string.IsNullOrWhiteSpace(input) || input.Length > MaximumEmbeddingInputCharacters || input.Contains('\0')))
        {
            throw new ArgumentException("Embedding input must contain 1 to 32 bounded nonempty strings.", nameof(inputs));
        }

        await RequireBoundModelAsync(
            model, expectedDigest, requireVision: false, requireEmbedding: true, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.PostAsJsonAsync(
            "api/embed",
            new { model, input = inputs, truncate = false, keep_alive = 0 },
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
        await RequireBoundModelAsync(
            model, expectedDigest, requireVision: false, requireEmbedding: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<OllamaVisionResult> DescribeImagePinnedAsync(
        string model,
        string expectedDigest,
        string imagePath,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
        ValidateDigest(expectedDigest, nameof(expectedDigest));
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

        await RequireBoundModelAsync(
            model, expectedDigest, requireVision: true, requireEmbedding: false, cancellationToken).ConfigureAwait(false);

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

        if (!HasSupportedImageSignature(imageBytes))
        {
            throw new ArgumentException("The image must be PNG, JPEG or WebP data.", nameof(imagePath));
        }

        var image = Convert.ToBase64String(imageBytes);
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
            model, expectedDigest, requireVision: true, requireEmbedding: false, cancellationToken).ConfigureAwait(false);
        return result;
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

    private async Task<LocalModelDescriptor> RequireBoundModelAsync(
        string model,
        string expectedDigest,
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
