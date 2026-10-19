using System.Net;
using System.Net.Sockets;

namespace DCx.Core.Documents;

public sealed class DownloadedScannedPdf : IDisposable
{
    private readonly string operationRoot;
    private bool disposed;

    internal DownloadedScannedPdf(string path, string operationRoot)
    {
        Path = path;
        this.operationRoot = operationRoot;
    }

    public string Path { get; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The private temporary contains only the explicitly downloaded PDF.
        }
    }
}

public sealed class ScannedPdfUrlDownloader : IDisposable
{
    private const long MaximumPdfBytes = 100L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri? allowedLoopbackFixtureOrigin;

    public ScannedPdfUrlDownloader(HttpMessageHandler handler, Uri? allowedLoopbackFixtureOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (allowedLoopbackFixtureOrigin is not null &&
            (!allowedLoopbackFixtureOrigin.IsAbsoluteUri || !allowedLoopbackFixtureOrigin.IsLoopback ||
             !string.Equals(allowedLoopbackFixtureOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
             allowedLoopbackFixtureOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(allowedLoopbackFixtureOrigin.Query) ||
             !string.IsNullOrEmpty(allowedLoopbackFixtureOrigin.Fragment)))
        {
            throw new ArgumentException("The PDF fixture origin must be an exact loopback HTTP root.", nameof(allowedLoopbackFixtureOrigin));
        }

        this.allowedLoopbackFixtureOrigin = allowedLoopbackFixtureOrigin;
        client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DCx-AgenticBrowser/1.0");
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/pdf"));
    }

    public static ScannedPdfUrlDownloader CreateProduction() => new(CreateProductionHandler());

    public async Task<DownloadedScannedPdf> DownloadAsync(
        string address,
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        var uri = new Uri(address, UriKind.Absolute);
        ValidateUri(uri);
        var root = Path.GetFullPath(Path.Combine(applicationDataRoot, "temporary-pdf-downloads"));
        Directory.CreateDirectory(root);
        if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The private PDF download root cannot be a reparse point.");
        var operationRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        var partial = Path.Combine(operationRoot, "download.partial");
        var completed = Path.Combine(operationRoot, "scanned-source.pdf");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentEncoding.Count != 0 ||
                response.Content.Headers.ContentLength is <= 0 or > MaximumPdfBytes)
            {
                throw new InvalidOperationException("The URL did not return an admitted bounded PDF response.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(
                             partial,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > MaximumPdfBytes)
                        throw new InvalidOperationException("The PDF response exceeded its streaming bound.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!IsCompletePdf(partial))
                throw new InvalidOperationException("The URL response was not a complete PDF file.");
            File.Move(partial, completed);
            return new DownloadedScannedPdf(completed, operationRoot);
        }
        catch
        {
            try { Directory.Delete(operationRoot, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose() => client.Dispose();

    public static HttpClientHandler CreateLoopbackFixtureHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseDefaultCredentials = false,
        UseProxy = false,
    };

    private void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("The scanned PDF URL is not admissible.");
        var isFixture = allowedLoopbackFixtureOrigin is not null &&
                        uri.Scheme == allowedLoopbackFixtureOrigin.Scheme &&
                        uri.Host == allowedLoopbackFixtureOrigin.Host &&
                        uri.Port == allowedLoopbackFixtureOrigin.Port;
        if (!isFixture && (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || uri.IsLoopback))
            throw new InvalidOperationException("Scanned PDF downloads require a public HTTPS URL.");
        if (!isFixture && IPAddress.TryParse(uri.Host, out var literalAddress) && !IsPublicAddress(literalAddress))
            throw new InvalidOperationException("Scanned PDF downloads deny non-public address literals.");
    }

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 32,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsPublicAddress)
                      ?? throw new HttpRequestException("The PDF host did not resolve to a public address.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xE0) == 0x20 &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
        return bytes[0] is >= 1 and <= 223 && bytes[0] != 10 && bytes[0] != 127 &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
               !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    private static bool IsCompletePdf(string path)
    {
        var file = new FileInfo(path);
        if (file.Length is < 128 or > MaximumPdfBytes) return false;
        using var stream = file.OpenRead();
        Span<byte> header = stackalloc byte[5];
        if (stream.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8)) return false;
        var tailLength = (int)Math.Min(2048, stream.Length);
        stream.Position = stream.Length - tailLength;
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);
        return System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
    }
}
