using System.Net;
using System.Text;
using DCx.Core.GlobalKnowledge;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class HttpGlobalRegistryTransportTests
{
    [Fact]
    public void Constructor_RejectsNonTlsRemoteEndpoint()
    {
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<GlobalReplicaException>(() =>
            new HttpGlobalRegistryTransport(client, new Uri("http://registry.example.test/")));
    }

    [Fact]
    public async Task Transport_ReadsDescriptorAndBoundedChunk()
    {
        var digest = new string('a', 64);
        var handler = new FixtureHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"sequence\":1,\"sha256\":\"{digest}\",\"sizeBytes\":4}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.Equal("offset=0&count=4", request.RequestUri.Query.TrimStart('?'));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
        });
        using var client = new HttpClient(handler);
        var transport = new HttpGlobalRegistryTransport(client, new Uri("https://registry.example.test/"));

        var descriptor = await transport.GetLatestReleaseAsync(CancellationToken.None);
        var chunk = await transport.DownloadReleaseChunkAsync(1, 0, 4, CancellationToken.None);

        Assert.Equal(1, descriptor!.Sequence);
        Assert.Equal([1, 2, 3, 4], chunk);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
