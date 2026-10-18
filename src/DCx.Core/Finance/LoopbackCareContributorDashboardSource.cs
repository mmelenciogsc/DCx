using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Finance;

public sealed class CareContributorDashboardTransportException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A validation-only client for the explicitly enabled literal-loopback contributor fixture.
/// Normal desktop launches do not instantiate this type.
/// </summary>
public sealed class LoopbackCareContributorDashboardSource :
    ICareContributorDashboardSource,
    IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly HttpClient httpClient;
    private readonly Uri origin;
    private readonly string expectedContributorPseudonym;
    private byte[]? contributorTokenBytes;

    public LoopbackCareContributorDashboardSource(
        Uri loopbackOrigin,
        string contributorToken,
        string expectedContributorPseudonym,
        TimeSpan? timeout = null)
        : this(
            new HttpClient(CreateHardenedHandler(), disposeHandler: true),
            loopbackOrigin,
            contributorToken,
            expectedContributorPseudonym,
            timeout)
    {
    }

    internal LoopbackCareContributorDashboardSource(
        HttpMessageHandler handler,
        Uri loopbackOrigin,
        string contributorToken,
        string expectedContributorPseudonym,
        TimeSpan? timeout = null)
        : this(
            new HttpClient(handler, disposeHandler: true),
            loopbackOrigin,
            contributorToken,
            expectedContributorPseudonym,
            timeout)
    {
    }

    private LoopbackCareContributorDashboardSource(
        HttpClient client,
        Uri loopbackOrigin,
        string contributorToken,
        string expectedContributorPseudonym,
        TimeSpan? timeout)
    {
        httpClient = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(loopbackOrigin);
        if (!loopbackOrigin.IsAbsoluteUri || loopbackOrigin.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(loopbackOrigin.Host, out var loopbackAddress) ||
            !IPAddress.IsLoopback(loopbackAddress) || loopbackOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(loopbackOrigin.Query) ||
            !string.IsNullOrEmpty(loopbackOrigin.Fragment) ||
            !string.IsNullOrEmpty(loopbackOrigin.UserInfo))
        {
            httpClient.Dispose();
            throw new ArgumentException(
                "CARE contributor simulation requires a root HTTP origin on a literal loopback IP address.",
                nameof(loopbackOrigin));
        }

        try
        {
            ValidateToken(contributorToken);
            PayPalSandboxValidation.RequireContributorPseudonym(expectedContributorPseudonym);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }

        var boundedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (boundedTimeout < TimeSpan.FromSeconds(1) || boundedTimeout > TimeSpan.FromMinutes(2))
        {
            httpClient.Dispose();
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        origin = loopbackOrigin;
        this.expectedContributorPseudonym = expectedContributorPseudonym;
        contributorTokenBytes = Encoding.UTF8.GetBytes(contributorToken);
        httpClient.BaseAddress = loopbackOrigin;
        httpClient.Timeout = boundedTimeout;
    }

    public async Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest();
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CareContributorDashboardTransportException(ReadErrorCode(body));
            }

            EnsureJsonResponse(response);
            var snapshot = Deserialize(body);
            CareContributorDashboardPolicy.RequireSnapshot(snapshot);
            if (snapshot.StatusCode != CareContributorDashboardPolicy.Available ||
                !string.Equals(
                    snapshot.ContributorPseudonym,
                    expectedContributorPseudonym,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The CARE contributor response identity does not match the authenticated subject.");
            }

            return snapshot;
        }
        catch (InvalidOperationException)
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_response_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        var bytes = Interlocked.Exchange(ref contributorTokenBytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private static SocketsHttpHandler CreateHardenedHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    private HttpRequestMessage CreateAuthorizedRequest()
    {
        var tokenBytes = contributorTokenBytes
            ?? throw new ObjectDisposedException(nameof(LoopbackCareContributorDashboardSource));
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/care/contributor-dashboard");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", Encoding.UTF8.GetString(tokenBytes));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_transport_failed");
        }

        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri ||
            responseUri.Scheme != Uri.UriSchemeHttp || !responseUri.IsLoopback ||
            !string.Equals(responseUri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) ||
            responseUri.Port != origin.Port)
        {
            response.Dispose();
            throw new CareContributorDashboardTransportException(
                "care_contributor_response_origin_invalid");
        }

        return response;
    }

    private static CareContributorDashboardSnapshot Deserialize(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<CareContributorDashboardSnapshot>(body, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_response_invalid");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void EnsureJsonResponse(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_response_invalid");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_response_invalid");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > MaximumResponseBytes)
                {
                    throw new CareContributorDashboardTransportException(
                        "care_contributor_response_invalid");
                }

                await output.WriteAsync(
                        buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            if (output.TryGetBuffer(out var written))
            {
                CryptographicOperations.ZeroMemory(written.AsSpan());
            }
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string ReadErrorCode(ReadOnlySpan<byte> body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
            return error?.Code is { Length: > 0 and <= 128 } code &&
                   code.All(character =>
                       char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? code
                : "care_contributor_rejected";
        }
        catch (JsonException)
        {
            return "care_contributor_rejected";
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 512 ||
            token.Any(character => character is < '!' or > '~'))
        {
            throw new CareContributorDashboardTransportException(
                "care_contributor_token_invalid");
        }
    }

    private sealed record ErrorResponse(string Code);
}
