using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.GlobalKnowledge;

public sealed class HttpGlobalRegistryTransport : IGlobalRegistryTransport
{
    public const int MaximumDescriptorBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    public HttpGlobalRegistryTransport(HttpClient httpClient, Uri endpoint)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.endpoint = GlobalRegistryEndpointPolicy.Normalize(endpoint);
    }

    public async Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(endpoint, "api/releases/latest");
        using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseOrigin(response, requestUri);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return ValidateReleaseDescriptor(
            await ReadDescriptorAsync<GlobalReleaseDescriptor>(response, cancellationToken)
                .ConfigureAwait(false));
    }

    public async Task<GlobalTrustPolicyDescriptor?> GetLatestTrustPolicyAsync(
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(endpoint, "api/trust-policy/latest");
        using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseOrigin(response, requestUri);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return ValidateTrustPolicyDescriptor(
            await ReadDescriptorAsync<GlobalTrustPolicyDescriptor>(response, cancellationToken)
                .ConfigureAwait(false));
    }

    public async Task<GlobalTrustPolicyDescriptor> GetTrustPolicyAsync(
        long revision,
        CancellationToken cancellationToken)
    {
        if (revision < 1)
        {
            throw new GlobalReplicaException("The requested trust-policy revision is invalid.");
        }

        var requestUri = new Uri(endpoint, $"api/trust-policy/{revision}");
        using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseOrigin(response, requestUri);
        response.EnsureSuccessStatusCode();
        return ValidateTrustPolicyDescriptor(
            await ReadDescriptorAsync<GlobalTrustPolicyDescriptor>(response, cancellationToken)
                .ConfigureAwait(false));
    }

    public Task<byte[]> DownloadTrustPolicyChunkAsync(
        long revision,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (revision < 1)
        {
            throw new GlobalReplicaException("The requested trust-policy revision is invalid.");
        }

        return DownloadChunkAsync(
            $"api/trust-policy/{revision}/content?offset={offset}&count={count}",
            offset,
            count,
            cancellationToken);
    }

    public async Task<GlobalReleaseDescriptor> GetReleaseAsync(
        long sequence,
        CancellationToken cancellationToken)
    {
        if (sequence < 1)
        {
            throw new GlobalReplicaException("The requested release sequence is invalid.");
        }

        var requestUri = new Uri(endpoint, $"api/releases/{sequence}");
        using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseOrigin(response, requestUri);
        response.EnsureSuccessStatusCode();
        return ValidateReleaseDescriptor(
            await ReadDescriptorAsync<GlobalReleaseDescriptor>(response, cancellationToken)
                .ConfigureAwait(false));
    }

    public Task<byte[]> DownloadReleaseChunkAsync(
        long sequence,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (sequence < 1)
        {
            throw new GlobalReplicaException("The requested release sequence is invalid.");
        }

        return DownloadChunkAsync(
            $"api/releases/{sequence}/content?offset={offset}&count={count}",
            offset,
            count,
            cancellationToken);
    }

    public Task<byte[]> DownloadArtifactChunkAsync(
        string sha256,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(sha256))
        {
            throw new GlobalReplicaException("The requested global artifact digest is invalid.");
        }

        return DownloadChunkAsync(
            $"api/artifacts/{sha256.ToLowerInvariant()}?offset={offset}&count={count}",
            offset,
            count,
            cancellationToken);
    }

    private async Task<byte[]> DownloadChunkAsync(
        string relativeUri,
        long offset,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || requestedCount is < 1 or > GlobalReplicaClient.TransferChunkBytes)
        {
            throw new GlobalReplicaException("The requested registry chunk range is invalid.");
        }

        var requestUri = new Uri(endpoint, relativeUri);
        using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseOrigin(response, requestUri);
        response.EnsureSuccessStatusCode();
        EnsureContentContract(
            response.Content,
            "application/octet-stream",
            allowUtf8Charset: false,
            "transfer");

        if (response.Content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > requestedCount))
        {
            throw new GlobalReplicaException("The registry returned an oversized transfer chunk.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream(requestedCount);
        var buffer = new byte[Math.Min(8192, requestedCount + 1)];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                if (output.Length > requestedCount)
                {
                    throw new GlobalReplicaException(
                        "The registry returned more bytes than requested.");
                }
            }

            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<T> ReadDescriptorAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        EnsureContentContract(
            response.Content,
            "application/json",
            allowUtf8Charset: true,
            "descriptor");

        if (response.Content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > MaximumDescriptorBytes))
        {
            throw new GlobalReplicaException("The registry returned an oversized descriptor.");
        }

        var body = await ReadBoundedDescriptorAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(JsonOptions)
                   ?? throw new GlobalReplicaException("The registry returned an empty descriptor.");
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new GlobalReplicaException("The registry returned an invalid descriptor.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<byte[]> ReadBoundedDescriptorAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumDescriptorBytes)
                {
                    throw new GlobalReplicaException(
                        "The registry returned an oversized descriptor.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
            {
                CryptographicOperations.ZeroMemory(segment.AsSpan(0, (int)output.Length));
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new GlobalReplicaException(
                            "The registry descriptor contains a duplicate property.");
                    }

                    RejectDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectDuplicateProperties(item);
                }

                break;
        }
    }

    private static void EnsureExactResponseOrigin(HttpResponseMessage response, Uri requestUri)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri ||
            !string.Equals(
                responseUri.AbsoluteUri,
                requestUri.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw new GlobalReplicaException("The registry response origin is invalid.");
        }
    }

    private static void EnsureContentContract(
        HttpContent content,
        string expectedMediaType,
        bool allowUtf8Charset,
        string label)
    {
        var contentType = content.Headers.ContentType;
        var charset = contentType?.CharSet?.Trim().Trim('"');
        if (!string.Equals(
                contentType?.MediaType,
                expectedMediaType,
                StringComparison.OrdinalIgnoreCase) ||
            content.Headers.ContentEncoding.Count != 0 ||
            (!string.IsNullOrEmpty(charset) &&
             (!allowUtf8Charset ||
              !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase))))
        {
            throw new GlobalReplicaException(
                $"The registry returned an invalid {label} content contract.");
        }
    }

    private static GlobalReleaseDescriptor ValidateReleaseDescriptor(
        GlobalReleaseDescriptor descriptor)
    {
        if (descriptor.Sequence < 1 || !IsSha256(descriptor.Sha256) ||
            descriptor.SizeBytes is <= 0 or > GlobalReleaseCodec.MaximumReleaseBytes)
        {
            throw new GlobalReplicaException(
                "The registry returned an invalid release descriptor.");
        }

        return descriptor;
    }

    private static GlobalTrustPolicyDescriptor ValidateTrustPolicyDescriptor(
        GlobalTrustPolicyDescriptor descriptor)
    {
        if (descriptor.Revision < 1 || !IsSha256(descriptor.Sha256) ||
            descriptor.SizeBytes is <= 0 or > GlobalTrustPolicyCodec.MaximumPolicyBytes)
        {
            throw new GlobalReplicaException(
                "The registry returned an invalid trust-policy descriptor.");
        }

        return descriptor;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public static class GlobalRegistryEndpointPolicy
{
    public const int MaximumEndpointCharacters = 2048;

    public static Uri Normalize(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.AbsoluteUri.Length > MaximumEndpointCharacters ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp &&
               IPAddress.TryParse(endpoint.Host, out var address) &&
               IPAddress.IsLoopback(address))))
        {
            throw new GlobalReplicaException(
                "A registry endpoint must be bounded HTTPS, or literal-loopback HTTP for local simulation.");
        }

        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }
}
