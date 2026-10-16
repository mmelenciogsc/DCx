using System.Net.Http.Json;
using System.Text.Json;

namespace DCx.Core.LocalModels;

public sealed class OllamaLocalClient
{
    private const long MaximumImageBytes = 20 * 1024 * 1024;
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var models = new List<LocalModelDescriptor>();
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            var details = item.GetProperty("details");
            models.Add(new LocalModelDescriptor(
                item.GetProperty("name").GetString()!,
                item.GetProperty("size").GetInt64(),
                SupportsVision: false,
                SupportsEmbeddings: false,
                details.TryGetProperty("parameter_size", out var parameterSize) ? parameterSize.GetString() : null,
                details.TryGetProperty("quantization_level", out var quantization) ? quantization.GetString() : null));
        }

        return models;
    }

    public async Task<OllamaVisionResult> DescribeImageAsync(
        string model,
        string imagePath,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var file = new FileInfo(Path.GetFullPath(imagePath));
        if (!file.Exists || file.Length is <= 0 or > MaximumImageBytes)
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        return new OllamaVisionResult(
            root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty,
            root.TryGetProperty("prompt_eval_count", out var promptTokens) ? promptTokens.GetInt32() : 0,
            root.TryGetProperty("eval_count", out var outputTokens) ? outputTokens.GetInt32() : 0,
            root.TryGetProperty("total_duration", out var duration) ? duration.GetInt64() : 0);
    }
}

public sealed record OllamaVisionResult(string Text, int PromptTokens, int OutputTokens, long TotalDurationNanoseconds);
