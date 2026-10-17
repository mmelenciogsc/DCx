using System.Net.Http.Json;
using System.Text.Json;

namespace DCx.Core.LocalModels;

public sealed class OllamaLocalClient
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private const int MaximumModelNameCharacters = 256;
    private const int MaximumPromptCharacters = 8192;
    private const int MaximumEmbeddingInputs = 32;
    private const int MaximumEmbeddingInputCharacters = 8192;
    private const int MaximumEmbeddingDimensions = 8192;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public OllamaLocalClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var baseAddress = httpClient.BaseAddress
            ?? throw new ArgumentException("Ollama HttpClient requires a base address.", nameof(httpClient));
        if (baseAddress.Scheme != Uri.UriSchemeHttp || !baseAddress.IsLoopback)
        {
            throw new ArgumentException("DCx permits the Ollama adapter only on loopback HTTP.", nameof(httpClient));
        }
    }

    public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/tags", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var models = new List<LocalModelDescriptor>();
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            if (models.Count >= 1000) throw new InvalidOperationException("The Ollama model inventory exceeds its limit.");
            var details = item.GetProperty("details");
            var capabilities = item.TryGetProperty("capabilities", out var capabilityElement)
                ? capabilityElement.EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal)
                : [];
            models.Add(new LocalModelDescriptor(
                item.GetProperty("name").GetString()!,
                item.GetProperty("size").GetInt64(),
                SupportsVision: capabilities.Contains("vision"),
                SupportsEmbeddings: capabilities.Contains("embedding"),
                details.TryGetProperty("parameter_size", out var parameterSize) ? parameterSize.GetString() : null,
                details.TryGetProperty("quantization_level", out var quantization) ? quantization.GetString() : null));
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

    public async Task<OllamaEmbeddingResult> EmbedAsync(
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is 0 or > MaximumEmbeddingInputs || inputs.Any(input =>
                string.IsNullOrWhiteSpace(input) || input.Length > MaximumEmbeddingInputCharacters || input.Contains('\0')))
        {
            throw new ArgumentException("Embedding input must contain 1 to 32 bounded nonempty strings.", nameof(inputs));
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "api/embed",
            new { model, input = inputs, truncate = false, keep_alive = 0 },
            cancellationToken).ConfigureAwait(false);
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

        return new OllamaEmbeddingResult(
            model,
            embeddings,
            root.TryGetProperty("prompt_eval_count", out var promptTokens) ? promptTokens.GetInt32() : 0,
            root.TryGetProperty("total_duration", out var duration) ? duration.GetInt64() : 0,
            root.TryGetProperty("load_duration", out var loadDuration) ? loadDuration.GetInt64() : 0);
    }

    public async Task<OllamaVisionResult> DescribeImageAsync(
        string model,
        string imagePath,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ValidateModelName(model);
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

        var image = Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt, images = new[] { image } } },
            stream = false,
            keep_alive = 0,
            options = new { temperature = 0, num_predict = 160 },
        };
        using var response = await _httpClient.PostAsJsonAsync("api/chat", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new OllamaVisionResult(
            root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty,
            root.TryGetProperty("prompt_eval_count", out var promptTokens) ? promptTokens.GetInt32() : 0,
            root.TryGetProperty("eval_count", out var outputTokens) ? outputTokens.GetInt32() : 0,
            root.TryGetProperty("total_duration", out var duration) ? duration.GetInt64() : 0);
    }

    private static void ValidateModelName(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > MaximumModelNameCharacters || model.Contains('\0') || model.Any(char.IsControl))
        {
            throw new ArgumentException("The Ollama model name is invalid.", nameof(model));
        }
    }

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
