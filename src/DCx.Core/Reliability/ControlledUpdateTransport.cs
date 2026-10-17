using System.Net;

namespace DCx.Core.Reliability;

public interface IControlledUpdateTransport
{
    Task<byte[]> DownloadManifestAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken);

    Task<long> DownloadPackageAsync(
        Uri uri,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class HttpControlledUpdateTransport : IControlledUpdateTransport, IDisposable
{
    public const string ManifestMediaType = "application/vnd.dcx.update.v1+json";

    private static readonly HashSet<string> PackageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/msix",
        "application/vnd.ms-appx",
        "application/octet-stream",
    };

    private readonly HttpClient httpClient;
    private readonly Uri allowedOrigin;

    public HttpControlledUpdateTransport(Uri allowedOrigin)
        : this(allowedOrigin, CreateHardenedHandler())
    {
    }

    internal HttpControlledUpdateTransport(Uri allowedOrigin, HttpMessageHandler handler)
    {
        this.allowedOrigin = ValidateOrigin(allowedOrigin);
        httpClient = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), true)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public async Task<byte[]> DownloadManifestAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > ControlledUpdateManifestCodec.MaximumEnvelopeBytes)
        {
            throw new ControlledUpdateException("update_manifest_bound_invalid");
        }

        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
        RequireMediaType(response, ManifestMediaType);
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new ControlledUpdateException("update_manifest_oversized");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task<long> DownloadPackageAsync(
        Uri uri,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || maximumBytes is < 1 or > ControlledUpdateManifestCodec.MaximumPackageBytes)
        {
            throw new ControlledUpdateException("update_package_bound_invalid");
        }

        using var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !PackageMediaTypes.Contains(mediaType))
        {
            throw new ControlledUpdateException("update_media_type_invalid");
        }

        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new ControlledUpdateException("update_package_oversized");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await CopyBoundedAsync(input, destination, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateRequestUri(uri);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is not { } finalUri ||
                !Uri.Equals(finalUri, uri) || !response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new ControlledUpdateException("update_transport_failed");
            }

            return response;
        }
        catch (ControlledUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            (exception is HttpRequestException or TaskCanceledException) &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new ControlledUpdateException("update_transport_failed");
        }
    }

    private void ValidateRequestUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.IdnHost, allowedOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != allowedOrigin.Port)
        {
            throw new ControlledUpdateException("update_origin_invalid");
        }
    }

    private static Uri ValidateOrigin(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
        {
            throw new ControlledUpdateException("update_origin_invalid");
        }

        return uri;
    }

    private static HttpMessageHandler CreateHardenedHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false,
        UseProxy = false,
    };

    private static void RequireMediaType(HttpResponseMessage response, string expected)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlledUpdateException("update_media_type_invalid");
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return total;
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new ControlledUpdateException("update_download_oversized");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
