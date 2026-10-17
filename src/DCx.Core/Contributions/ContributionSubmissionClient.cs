using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Contributions;

public sealed record ContributionSubmissionResult(string SubmissionId, ContributionState State);

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
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/contributions/intake");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", contributorToken);
            request.Content = new ByteArrayContent(envelope);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContributionTransportCodec.MediaType);
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureResponseOrigin(response);
            var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                throw new ContributionSubmissionException(ReadErrorCode(body));
            }

            if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            {
                throw new ContributionSubmissionException("submission_response_invalid");
            }

            AcceptedResponse? accepted;
            try
            {
                accepted = JsonSerializer.Deserialize<AcceptedResponse>(body, JsonOptions);
            }
            catch (JsonException)
            {
                throw new ContributionSubmissionException("submission_response_invalid");
            }

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
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private void EnsureResponseOrigin(HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !responseUri.IsAbsoluteUri || responseUri.Scheme != Uri.UriSchemeHttp ||
            !responseUri.IsLoopback || !string.Equals(responseUri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) ||
            responseUri.Port != origin.Port)
        {
            throw new ContributionSubmissionException("submission_response_origin_invalid");
        }
    }

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
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new ContributionSubmissionException("submission_response_invalid");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
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

    private static string ReadErrorCode(ReadOnlySpan<byte> body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
            return error?.Code is { Length: > 0 and <= 128 } code &&
                   code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? code
                : "submission_rejected";
        }
        catch (JsonException)
        {
            return "submission_rejected";
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
    private sealed record ErrorResponse(string Code);
}
