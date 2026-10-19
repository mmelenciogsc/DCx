using System.Net;
using System.Net.Http.Headers;
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
            var content = new ByteArrayContent([1, 2, 3, 4]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });
        using var client = new HttpClient(handler);
        var transport = new HttpGlobalRegistryTransport(client, new Uri("https://registry.example.test/"));

        var descriptor = await transport.GetLatestReleaseAsync(CancellationToken.None);
        var chunk = await transport.DownloadReleaseChunkAsync(1, 0, 4, CancellationToken.None);

        Assert.Equal(1, descriptor!.Sequence);
        Assert.Equal([1, 2, 3, 4], chunk);
    }

    [Theory]
    [InlineData("text/plain", "{\"sequence\":1,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sizeBytes\":4}")]
    [InlineData("application/json", "{\"sequence\":1,\"sequence\":2,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sizeBytes\":4}")]
    [InlineData("application/json", "{\"sequence\":1,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sizeBytes\":4,\"extra\":true}")]
    [InlineData("application/json", "{\"sequence\":0,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sizeBytes\":4}")]
    [InlineData("application/json", "{\"sequence\":1,\"sha256\":\"short\",\"sizeBytes\":4}")]
    [InlineData("application/json", "{\"sequence\":1,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sizeBytes\":16777217}")]
    public async Task Transport_RejectsNonCanonicalDescriptorResponses(
        string mediaType,
        string body)
    {
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transport_RejectsDeclaredAndStreamingOversizedDescriptors(
        bool declaredLength)
    {
        var bytes = new byte[HttpGlobalRegistryTransport.MaximumDescriptorBytes + 1];
        Array.Fill(bytes, (byte)' ');
        HttpContent content = declaredLength
            ? new ByteArrayContent(bytes)
            : new UnknownLengthContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transport_RejectsChangedResponseOrigin()
    {
        var digest = new string('a', 64);
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://other.example.test/api/releases/latest"),
            Content = new StringContent(
                $"{{\"sequence\":1,\"sha256\":\"{digest}\",\"sizeBytes\":4}}",
                Encoding.UTF8,
                "application/json"),
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("utf-16", null)]
    [InlineData("utf-8", "gzip")]
    public async Task Transport_RejectsAmbiguousDescriptorEncoding(
        string charset,
        string? contentEncoding)
    {
        var digest = new string('a', 64);
        var content = new StringContent(
            $"{{\"sequence\":1,\"sha256\":\"{digest}\",\"sizeBytes\":4}}",
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentType!.CharSet = charset;
        if (contentEncoding is not null) content.Headers.ContentEncoding.Add(contentEncoding);
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("text/plain", 4, false)]
    [InlineData("application/octet-stream", 5, false)]
    [InlineData("application/octet-stream", 5, true)]
    public async Task Transport_RejectsWrongMediaTypeAndOversizedChunks(
        string mediaType,
        int responseBytes,
        bool unknownLength)
    {
        HttpContent content = unknownLength
            ? new UnknownLengthContent(new byte[responseBytes])
            : new ByteArrayContent(new byte[responseBytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadReleaseChunkAsync(1, 0, 4, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_RejectsCharsetOnBinaryChunk()
    {
        var content = new ByteArrayContent([1, 2, 3, 4]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream")
        {
            CharSet = "utf-8",
        };
        using var client = new HttpClient(new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadReleaseChunkAsync(1, 0, 4, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_RejectsInvalidRequestsBeforeNetworkContact()
    {
        var requests = 0;
        using var client = new HttpClient(new FixtureHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var transport = new HttpGlobalRegistryTransport(
            client,
            new Uri("https://registry.example.test/"));

        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetReleaseAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.GetTrustPolicyAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadReleaseChunkAsync(1, -1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadReleaseChunkAsync(1, 0, 0, CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(() => transport.DownloadReleaseChunkAsync(
            1,
            0,
            GlobalReplicaClient.TransferChunkBytes + 1,
            CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadTrustPolicyChunkAsync(0, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<GlobalReplicaException>(
            () => transport.DownloadArtifactChunkAsync("invalid", 0, 1, CancellationToken.None));

        Assert.Equal(0, requests);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
