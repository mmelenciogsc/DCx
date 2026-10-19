using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DCx.Core.Documents;

namespace DCx.Core.Tests.Documents;

public sealed class ScannedPdfUrlDownloaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-pdf-url-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactLoopbackFixture_DownloadsCompletePdfAndDeletesItOnDispose()
    {
        var origin = new Uri("http://127.0.0.1:43187/");
        var handler = new StaticHandler(request =>
        {
            Assert.Equal(new Uri(origin, "fixture.pdf"), request.RequestUri);
            return PdfResponse(CompletePdf());
        });
        using var downloader = new ScannedPdfUrlDownloader(handler, origin);

        var downloaded = await downloader.DownloadAsync(new Uri(origin, "fixture.pdf").AbsoluteUri, root);

        Assert.True(File.Exists(downloaded.Path));
        Assert.StartsWith("%PDF-", await File.ReadAllTextAsync(downloaded.Path), StringComparison.Ordinal);
        var operationRoot = Path.GetDirectoryName(downloaded.Path)!;
        downloaded.Dispose();
        Assert.False(Directory.Exists(operationRoot));
    }

    [Theory]
    [InlineData("http://example.com/file.pdf")]
    [InlineData("https://user@example.com/file.pdf")]
    [InlineData("https://example.com/file.pdf#section")]
    [InlineData("http://127.0.0.1:43188/file.pdf")]
    public async Task UnsafeOrWrongOriginUrl_IsRejectedBeforeRequest(string address)
    {
        var handler = new StaticHandler(_ => throw new InvalidOperationException("A request must not be sent."));
        using var downloader = new ScannedPdfUrlDownloader(handler, new Uri("http://127.0.0.1:43187/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(address, root));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RedirectWrongMediaAndIncompletePdf_AreRejectedWithoutResidue()
    {
        var origin = new Uri("http://127.0.0.1:43187/");
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri(origin, "other.pdf") },
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not pdf", Encoding.UTF8, "text/plain"),
            },
            PdfResponse(Encoding.ASCII.GetBytes("%PDF-1.7 incomplete")),
        ]);
        using var downloader = new ScannedPdfUrlDownloader(
            new StaticHandler(_ => responses.Dequeue()),
            origin);

        for (var index = 0; index < 3; index++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                downloader.DownloadAsync(new Uri(origin, "fixture.pdf").AbsoluteUri, root));
        }

        var downloadRoot = Path.Combine(root, "temporary-pdf-downloads");
        Assert.True(!Directory.Exists(downloadRoot) || !Directory.EnumerateFileSystemEntries(downloadRoot).Any());
    }

    [Theory]
    [InlineData("https://10.1.2.3/private.pdf")]
    [InlineData("https://169.254.169.254/metadata.pdf")]
    [InlineData("https://192.168.1.5/private.pdf")]
    [InlineData("https://[::1]/private.pdf")]
    [InlineData("https://[fc00::1]/private.pdf")]
    public async Task ProductionPolicy_RejectsPrivateAddressLiteralBeforeRequest(string address)
    {
        var handler = new StaticHandler(_ => throw new InvalidOperationException("A request must not be sent."));
        using var downloader = new ScannedPdfUrlDownloader(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(address, root));

        Assert.Equal(0, handler.Requests);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static HttpResponseMessage PdfResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return response;
    }

    private static byte[] CompletePdf()
    {
        var body = "%PDF-1.7\n" + new string(' ', 160) + "\n%%EOF\n";
        return Encoding.ASCII.GetBytes(body);
    }

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }
}
