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

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string ChatRequest { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                return Json("{\"models\":[{\"name\":\"fixture-vlm\",\"size\":1000,\"details\":{\"parameter_size\":\"1B\",\"quantization_level\":\"Q4\"}}]}");
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
