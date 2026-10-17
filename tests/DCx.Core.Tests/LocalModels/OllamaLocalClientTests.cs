using System.Net;
using System.Text;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class OllamaLocalClientTests
{
    [Fact]
    public void Constructor_RejectsNonLoopbackEndpoint()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://example.com/") };

        Assert.Throws<ArgumentException>(() => new OllamaLocalClient(httpClient));
    }

    [Fact]
    public async Task ListAndVision_UseExpectedLocalContracts()
    {
        var handler = new FixtureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var client = new OllamaLocalClient(httpClient);
        var image = Path.GetTempFileName();
        await File.WriteAllBytesAsync(image, [1, 2, 3]);
        try
        {
            var models = await client.ListModelsAsync();
            var result = await client.DescribeImageAsync("fixture-vlm", image, "Describe shapes.");

            Assert.Equal("fixture-vlm", Assert.Single(models).Name);
            Assert.True(models.Single().SupportsVision);
            Assert.Equal("A blue square. A red circle.", result.Text);
            Assert.Equal(12, result.PromptTokens);
            Assert.Equal(8, result.OutputTokens);
            Assert.Contains("\"stream\":false", handler.ChatRequest, StringComparison.Ordinal);
            Assert.Contains("\"images\":[\"AQID\"]", handler.ChatRequest, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task InspectionAndEmbedding_ValidateCapabilitiesAndBoundedVectors()
    {
        var handler = new FixtureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        var client = new OllamaLocalClient(httpClient);

        var inspection = await client.InspectModelAsync("fixture-embed");
        var result = await client.EmbedAsync("fixture-embed", ["first", "second"]);

        Assert.Contains("embedding", inspection.Capabilities);
        Assert.Equal("Apache-2.0", inspection.License);
        Assert.Equal(2, result.Embeddings.Count);
        Assert.All(result.Embeddings, vector => Assert.Equal(3, vector.Length));
        Assert.Equal(7, result.PromptTokens);
        Assert.Contains("\"truncate\":false", handler.EmbeddingRequest, StringComparison.Ordinal);
        Assert.Contains("\"keep_alive\":0", handler.EmbeddingRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedding_RejectsInvalidInputAndInconsistentResponseDimensions()
    {
        var handler = new FixtureHandler { ReturnInconsistentEmbeddings = true };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var client = new OllamaLocalClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync("fixture", []));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync("fixture", [new string('x', 8193)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync("fixture", ["one", "two"]));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string ChatRequest { get; private set; } = string.Empty;
        public string EmbeddingRequest { get; private set; } = string.Empty;
        public bool ReturnInconsistentEmbeddings { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                return Json("{\"models\":[{\"name\":\"fixture-vlm\",\"size\":1000,\"capabilities\":[\"completion\",\"vision\"],\"details\":{\"parameter_size\":\"1B\",\"quantization_level\":\"Q4\"}}]}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/show")
            {
                return Json("{\"license\":\"Apache-2.0\",\"capabilities\":[\"embedding\"],\"details\":{\"family\":\"bert\",\"parameter_size\":\"300M\",\"quantization_level\":\"F16\"}}");
            }

            if (request.RequestUri?.AbsolutePath == "/api/embed")
            {
                EmbeddingRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(ReturnInconsistentEmbeddings
                    ? "{\"model\":\"fixture\",\"embeddings\":[[0.1,0.2],[0.3]],\"prompt_eval_count\":7,\"total_duration\":100}"
                    : "{\"model\":\"fixture-embed\",\"embeddings\":[[0.1,0.2,0.3],[0.4,0.5,0.6]],\"prompt_eval_count\":7,\"total_duration\":100,\"load_duration\":20}");
            }

            ChatRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"message\":{\"content\":\"A blue square. A red circle.\"},\"prompt_eval_count\":12,\"eval_count\":8,\"total_duration\":1000}");
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
