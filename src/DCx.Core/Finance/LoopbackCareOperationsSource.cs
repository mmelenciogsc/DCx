using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Finance;

public sealed record CareOperationsProcessRequest(
    CareOperationsHealth ReviewedHealth,
    bool ExplicitlyApproved);

public sealed class CareOperationsTransportException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A validation-only source for the explicitly enabled literal-loopback global-service fixture.
/// Normal desktop launches do not instantiate this type.
/// </summary>
public sealed class LoopbackCareOperationsSource :
    ICareOperationsSource,
    ICareBlockedCaseReviewSource,
    ICareFailedPayoutReviewSource,
    IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly HttpClient httpClient;
    private readonly Uri origin;
    private byte[]? operatorTokenBytes;

    public LoopbackCareOperationsSource(
        Uri loopbackOrigin,
        string operatorToken,
        TimeSpan? timeout = null)
        : this(new HttpClient(CreateHardenedHandler(), disposeHandler: true), loopbackOrigin, operatorToken, timeout)
    {
    }

    internal LoopbackCareOperationsSource(
        HttpMessageHandler handler,
        Uri loopbackOrigin,
        string operatorToken,
        TimeSpan? timeout = null)
        : this(new HttpClient(handler, disposeHandler: true), loopbackOrigin, operatorToken, timeout)
    {
    }

    private LoopbackCareOperationsSource(
        HttpClient client,
        Uri loopbackOrigin,
        string operatorToken,
        TimeSpan? timeout)
    {
        httpClient = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(loopbackOrigin);
        if (!loopbackOrigin.IsAbsoluteUri || loopbackOrigin.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(loopbackOrigin.Host, out var loopbackAddress) ||
            !IPAddress.IsLoopback(loopbackAddress) || loopbackOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(loopbackOrigin.Query) || !string.IsNullOrEmpty(loopbackOrigin.Fragment) ||
            !string.IsNullOrEmpty(loopbackOrigin.UserInfo))
        {
            httpClient.Dispose();
            throw new ArgumentException(
                "CARE simulation operations require a root HTTP origin on a literal loopback IP address.",
                nameof(loopbackOrigin));
        }

        ValidateToken(operatorToken);
        var boundedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (boundedTimeout < TimeSpan.FromSeconds(1) || boundedTimeout > TimeSpan.FromMinutes(2))
        {
            httpClient.Dispose();
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        origin = loopbackOrigin;
        operatorTokenBytes = Encoding.UTF8.GetBytes(operatorToken);
        httpClient.BaseAddress = loopbackOrigin;
        httpClient.Timeout = boundedTimeout;
    }

    public async Task<CareOperationsHealth> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "api/care/operations/health");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureJsonResponse(response);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CareOperationsTransportException(ReadErrorCode(body));
            }

            var health = Deserialize<CareOperationsHealth>(body);
            CareOperationsPolicy.RequireHealth(health);
            return health;
        }
        catch (InvalidOperationException)
        {
            throw new CareOperationsTransportException("care_operations_response_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        CareOperationsPolicy.RequireHealth(reviewedHealth);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new CareOperationsProcessRequest(reviewedHealth, explicitlyApproved),
            JsonOptions);
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/care/operations/process");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureJsonResponse(response);
            var responseBody = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            try
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new CareOperationsTransportException(ReadErrorCode(responseBody));
                }

                var result = Deserialize<CareOperationsProcessResult>(responseBody);
                CareOperationsPolicy.RequireProcessResult(result);
                return result;
            }
            catch (InvalidOperationException)
            {
                throw new CareOperationsTransportException("care_operations_response_invalid");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBody);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get, "api/care/operations/blocked-cases");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureJsonResponse(response);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CareOperationsTransportException(ReadErrorCode(body));
            }

            var cases = Deserialize<List<CareBlockedCaseReview>>(body);
            CareBlockedCaseReviewPolicy.RequireList(cases);
            return cases;
        }
        catch (InvalidOperationException)
        {
            throw new CareOperationsTransportException("care_blocked_cases_response_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<CareBlockedCaseDispositionResult> RecordDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        CareBlockedCaseReviewPolicy.RequireRequest(request);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        try
        {
            using var message = CreateAuthorizedRequest(
                HttpMethod.Post,
                $"api/care/operations/blocked-cases/{request.CaseId}/disposition");
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
            EnsureJsonResponse(response);
            var responseBody = await ReadBoundedAsync(
                    response.Content, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new CareOperationsTransportException(ReadErrorCode(responseBody));
                }

                var result = Deserialize<CareBlockedCaseDispositionResult>(responseBody);
                CareBlockedCaseReviewPolicy.RequireResult(result);
                if (!string.Equals(result.CaseId, request.CaseId, StringComparison.Ordinal) ||
                    !string.Equals(
                        result.DispositionCode, request.DispositionCode, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The CARE blocked-case result does not match the request.");
                }

                return result;
            }
            catch (InvalidOperationException)
            {
                throw new CareOperationsTransportException(
                    "care_blocked_case_response_invalid");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBody);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get, "api/care/operations/failed-payout-holds");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureJsonResponse(response);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new CareOperationsTransportException(ReadErrorCode(body));
            }

            var reviews = Deserialize<List<CareFailedPayoutReview>>(body);
            CareFailedPayoutReviewPolicy.RequireList(reviews);
            return reviews;
        }
        catch (InvalidOperationException)
        {
            throw new CareOperationsTransportException(
                "care_failed_payout_holds_response_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        CareFailedPayoutReviewPolicy.RequireRequest(request);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        try
        {
            using var message = CreateAuthorizedRequest(
                HttpMethod.Post,
                $"api/care/operations/failed-payout-holds/{request.ReviewId}/disposition");
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
            EnsureJsonResponse(response);
            var responseBody = await ReadBoundedAsync(
                    response.Content, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new CareOperationsTransportException(ReadErrorCode(responseBody));
                }

                var result = Deserialize<CareFailedPayoutDispositionResult>(responseBody);
                CareFailedPayoutReviewPolicy.RequireResult(result, request);
                return result;
            }
            catch (InvalidOperationException)
            {
                throw new CareOperationsTransportException(
                    "care_failed_payout_response_invalid");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBody);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        var bytes = Interlocked.Exchange(ref operatorTokenBytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    internal static SocketsHttpHandler CreateHardenedHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 2,
        MaxResponseHeadersLength = 16,
    };

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string requestUri)
    {
        var tokenBytes = operatorTokenBytes
            ?? throw new ObjectDisposedException(nameof(LoopbackCareOperationsSource));
        var request = new HttpRequestMessage(method, new Uri(origin, requestUri));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", Encoding.UTF8.GetString(tokenBytes));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
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
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new CareOperationsTransportException("care_operations_transport_failed");
        }

        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri || request.RequestUri is null ||
            !string.Equals(
                responseUri.AbsoluteUri,
                request.RequestUri.AbsoluteUri,
                StringComparison.Ordinal))
        {
            response.Dispose();
            throw new CareOperationsTransportException("care_operations_response_origin_invalid");
        }

        return response;
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new CareOperationsTransportException("care_operations_response_invalid");
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
                    if (!names.Add(property.Name)) throw new JsonException();
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

    private static void EnsureJsonResponse(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType;
        var charset = contentType?.CharSet?.Trim().Trim('"');
        if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            (!string.IsNullOrEmpty(charset) &&
             !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CareOperationsTransportException("care_operations_response_invalid");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > MaximumResponseBytes))
        {
            throw new CareOperationsTransportException("care_operations_response_invalid");
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
                    throw new CareOperationsTransportException("care_operations_response_invalid");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            if (output.TryGetBuffer(out var written)) CryptographicOperations.ZeroMemory(written.AsSpan());
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string ReadErrorCode(byte[] body)
    {
        try
        {
            var error = Deserialize<ErrorResponse>(body);
            return error?.Code is { Length: > 0 and <= 128 } code &&
                   code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? code
                : "care_operations_rejected";
        }
        catch (Exception exception) when (
            exception is JsonException or CareOperationsTransportException)
        {
            return "care_operations_rejected";
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 512 ||
            token.Any(character => character is < '!' or > '~'))
        {
            throw new CareOperationsTransportException("care_operator_token_invalid");
        }
    }

    private sealed record ErrorResponse(string Code);
}
