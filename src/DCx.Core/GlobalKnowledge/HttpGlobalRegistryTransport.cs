using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DCx.Core.GlobalKnowledge;

public sealed class HttpGlobalRegistryTransport : IGlobalRegistryTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    public HttpGlobalRegistryTransport(HttpClient httpClient, Uri endpoint)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))))
        {
            throw new GlobalReplicaException("A registry endpoint must be HTTPS, or loopback HTTP for local simulation.");
        }

        this.endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public async Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(new Uri(endpoint, "api/releases/latest"), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GlobalReleaseDescriptor>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new GlobalReplicaException("The registry returned an empty latest-release descriptor.");
    }

    public async Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
                new Uri(endpoint, $"api/releases/{sequence}"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GlobalReleaseDescriptor>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new GlobalReplicaException("The registry returned an empty release descriptor.");
    }

    public Task<byte[]> DownloadReleaseChunkAsync(
        long sequence,
        long offset,
        int count,
        CancellationToken cancellationToken) =>
        DownloadChunkAsync($"api/releases/{sequence}/content?offset={offset}&count={count}", count, cancellationToken);

    public Task<byte[]> DownloadArtifactChunkAsync(
        string sha256,
        long offset,
        int count,
        CancellationToken cancellationToken) =>
        DownloadChunkAsync(
            $"api/artifacts/{Uri.EscapeDataString(sha256)}?offset={offset}&count={count}",
            count,
            cancellationToken);

    private async Task<byte[]> DownloadChunkAsync(
        string relativeUri,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        if (requestedCount is < 1 or > GlobalReplicaClient.TransferChunkBytes)
        {
            throw new GlobalReplicaException("The requested registry chunk size is invalid.");
        }

        using var response = await httpClient.GetAsync(new Uri(endpoint, relativeUri), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > GlobalReplicaClient.TransferChunkBytes)
        {
            throw new GlobalReplicaException("The registry returned an oversized transfer chunk.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(requestedCount);
        var buffer = new byte[Math.Min(8192, requestedCount + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > requestedCount)
            {
                throw new GlobalReplicaException("The registry returned more bytes than requested.");
            }
        }

        return output.ToArray();
    }
}
