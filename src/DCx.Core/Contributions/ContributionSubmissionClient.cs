using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.GlobalKnowledge;

namespace DCx.Core.Contributions;

public sealed record ContributionSubmissionResult(string SubmissionId, ContributionState State);

public sealed record ContributionSimulationStatus(
    string SubmissionId,
    string CandidateSha256,
    ContributionState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ReviewedUtc,
    long? ReleaseSequence);

public sealed record ContributionWithdrawalResult(
    string SubmissionId,
    string CandidateSha256,
    ContributionState State,
    GlobalReleaseDescriptor Release);

public sealed class ContributionSubmissionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class ContributionSubmissionClient : IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly HttpClient httpClient;
    private readonly Uri origin;

    public ContributionSubmissionClient(Uri loopbackOrigin, TimeSpan? timeout = null)
        : this(new HttpClient(CreateHardenedHandler(), disposeHandler: true), loopbackOrigin, timeout)
    {
    }

    internal ContributionSubmissionClient(
        HttpMessageHandler handler,
        Uri loopbackOrigin,
        TimeSpan? timeout = null)
        : this(new HttpClient(handler, disposeHandler: true), loopbackOrigin, timeout)
    {
    }

    private ContributionSubmissionClient(HttpClient client, Uri loopbackOrigin, TimeSpan? timeout)
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
                "Contribution simulation submission requires a root HTTP origin on a literal loopback IP address.",
                nameof(loopbackOrigin));
        }

        var boundedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (boundedTimeout < TimeSpan.FromSeconds(1) || boundedTimeout > TimeSpan.FromMinutes(2))
        {
            httpClient.Dispose();
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        origin = loopbackOrigin;
        httpClient.BaseAddress = loopbackOrigin;
        httpClient.Timeout = boundedTimeout;
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

    public async Task<ContributionSubmissionResult> SubmitAsync(
        PreparedContribution contribution,
        string contributorToken,
        ReadOnlyMemory<byte> artifact = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ValidateToken(contributorToken);
        byte[] envelope;
        try
        {
            envelope = ContributionTransportCodec.Encode(contribution, artifact.Span);
        }
        catch (ContributionPolicyException)
        {
            throw new ContributionSubmissionException("submission_envelope_invalid");
        }

        try
        {
            var requestUri = new Uri(origin, "api/contributions/intake");
            using var request = CreateAuthorizedRequest(HttpMethod.Post, requestUri, contributorToken);
            request.Content = new ByteArrayContent(envelope);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContributionTransportCodec.MediaType);
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureExactResponseUri(response, requestUri);
            EnsureJsonResponse(response);
            var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            try
            {
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    throw new ContributionSubmissionException(ReadErrorCode(body));
                }

                var accepted = DeserializeResponse<AcceptedResponse>(body);

                if (accepted is null || accepted.SubmissionId != contribution.SubmissionId ||
                    accepted.State != ContributionState.Quarantined ||
                    response.Headers.Location is null ||
                    !LocationMatches(response.Headers.Location, contribution.SubmissionId))
                {
                    throw new ContributionSubmissionException("submission_response_invalid");
                }

                return new ContributionSubmissionResult(accepted.SubmissionId, accepted.State);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public async Task<ContributionSimulationStatus> GetStatusAsync(
        PreparedContribution contribution,
        string contributorToken,
        CancellationToken cancellationToken = default)
    {
        var expectedCandidateSha256 = ValidatePreparedContribution(contribution);
        ValidateToken(contributorToken);
        var requestUri = new Uri(
            origin,
            $"api/contributions/{Uri.EscapeDataString(contribution.SubmissionId)}");
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            requestUri,
            contributorToken);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseUri(response, requestUri);
        EnsureJsonResponse(response);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ContributionSubmissionException(ReadErrorCode(body));
            }

            var status = DeserializeResponse<OwnerStatusResponse>(body);

            if (status is null ||
                !string.Equals(status.SubmissionId, contribution.SubmissionId, StringComparison.Ordinal) ||
                !string.Equals(status.CandidateSha256, expectedCandidateSha256, StringComparison.Ordinal) ||
                !StatusFieldsAreValid(status.State, status.CreatedUtc, status.ReviewedUtc, status.ReleaseSequence))
            {
                throw new ContributionSubmissionException("submission_response_invalid");
            }

            return new ContributionSimulationStatus(
                status.SubmissionId,
                status.CandidateSha256,
                status.State,
                status.CreatedUtc,
                status.ReviewedUtc,
                status.ReleaseSequence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public async Task<ContributionWithdrawalResult> WithdrawAsync(
        PreparedContribution contribution,
        string contributorToken,
        CancellationToken cancellationToken = default)
    {
        var expectedCandidateSha256 = ValidatePreparedContribution(contribution);
        ValidateToken(contributorToken);
        var requestUri = new Uri(
            origin,
            $"api/contributions/{Uri.EscapeDataString(contribution.SubmissionId)}/withdraw");
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            requestUri,
            contributorToken);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureExactResponseUri(response, requestUri);
        EnsureJsonResponse(response);
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ContributionSubmissionException(ReadErrorCode(body));
            }

            var withdrawal = DeserializeResponse<OwnerWithdrawalResponse>(body);

            if (withdrawal is null ||
                !string.Equals(withdrawal.SubmissionId, contribution.SubmissionId, StringComparison.Ordinal) ||
                !string.Equals(withdrawal.CandidateSha256, expectedCandidateSha256, StringComparison.Ordinal) ||
                withdrawal.State != ContributionState.Withdrawn ||
                withdrawal.Release is null || withdrawal.Release.Sequence <= 0 ||
                withdrawal.Release.SizeBytes <= 0 || !IsSha256(withdrawal.Release.Sha256))
            {
                throw new ContributionSubmissionException("submission_response_invalid");
            }

            return new ContributionWithdrawalResult(
                withdrawal.SubmissionId,
                withdrawal.CandidateSha256,
                withdrawal.State,
                withdrawal.Release);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static void EnsureExactResponseUri(HttpResponseMessage response, Uri requestUri)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri ||
            !string.Equals(responseUri.AbsoluteUri, requestUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new ContributionSubmissionException("submission_response_origin_invalid");
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        Uri requestUri,
        string contributorToken)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", contributorToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static void EnsureJsonResponse(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType;
        var charset = contentType?.CharSet?.Trim().Trim('"');
        if (!string.Equals(
                contentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            (!string.IsNullOrEmpty(charset) &&
             !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ContributionSubmissionException("submission_response_invalid");
        }
    }

    private static string ValidatePreparedContribution(PreparedContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(contribution.SubmissionId) ||
            contribution.State != ContributionState.Quarantined)
        {
            throw new ContributionSubmissionException("submission_envelope_invalid");
        }

        try
        {
            return ContributionCodec.CandidateSha256(contribution.Candidate);
        }
        catch
        {
            throw new ContributionSubmissionException("submission_envelope_invalid");
        }
    }

    private static bool StatusFieldsAreValid(
        ContributionState state,
        DateTimeOffset createdUtc,
        DateTimeOffset? reviewedUtc,
        long? releaseSequence)
    {
        if (!Enum.IsDefined(state) || createdUtc.Offset != TimeSpan.Zero ||
            reviewedUtc is not null &&
            (reviewedUtc.Value.Offset != TimeSpan.Zero || reviewedUtc.Value < createdUtc))
        {
            return false;
        }

        return state switch
        {
            ContributionState.Quarantined => reviewedUtc is null && releaseSequence is null,
            ContributionState.Approved or ContributionState.Withdrawn =>
                reviewedUtc is not null && releaseSequence is > 0,
            ContributionState.Rejected => reviewedUtc is not null && releaseSequence is null,
            _ => false,
        };
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private bool LocationMatches(Uri location, string submissionId)
    {
        var expected = $"/api/contributions/{Uri.EscapeDataString(submissionId)}";
        if (!location.IsAbsoluteUri) return location.OriginalString == expected;
        return location.Scheme == Uri.UriSchemeHttp && location.IsLoopback &&
               string.Equals(location.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
               location.Port == origin.Port && location.AbsolutePath == expected &&
               string.IsNullOrEmpty(location.Query) && string.IsNullOrEmpty(location.Fragment) &&
               string.IsNullOrEmpty(location.UserInfo);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > MaximumResponseBytes))
        {
            throw new ContributionSubmissionException("submission_response_invalid");
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
                    throw new ContributionSubmissionException("submission_response_invalid");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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

    private static string ReadErrorCode(byte[] body)
    {
        try
        {
            var error = DeserializeResponse<ErrorResponse>(body);
            return error?.Code is { Length: > 0 and <= 128 } code &&
                   code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? code
                : "submission_rejected";
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ContributionSubmissionException)
        {
            return "submission_rejected";
        }
    }

    private static T DeserializeResponse<T>(ReadOnlyMemory<byte> body) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(body, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(JsonOptions)
                   ?? throw new ContributionSubmissionException("submission_response_invalid");
        }
        catch (ContributionSubmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ContributionSubmissionException("submission_response_invalid");
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
                        throw new ContributionSubmissionException("submission_response_invalid");
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

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 512 ||
            token.Any(character => character is < '!' or > '~'))
        {
            throw new ContributionSubmissionException("contributor_token_invalid");
        }
    }

    private sealed record AcceptedResponse(string SubmissionId, ContributionState State);
    private sealed record OwnerStatusResponse(
        string SubmissionId,
        string CandidateSha256,
        ContributionState State,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? ReviewedUtc,
        long? ReleaseSequence);
    private sealed record OwnerWithdrawalResponse(
        string SubmissionId,
        string CandidateSha256,
        ContributionState State,
        GlobalReleaseDescriptor Release);
    private sealed record ErrorResponse(string Code);
}
