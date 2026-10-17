using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCx.Core.Finance;

public sealed class PayPalSandboxException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IPayPalSandboxCredentialSource
{
    ValueTask<PayPalSandboxCredentialLease> AcquireAsync(CancellationToken cancellationToken);
}

public sealed class PayPalSandboxCredentialLease : IDisposable
{
    private byte[]? clientId;
    private byte[]? clientSecret;

    private PayPalSandboxCredentialLease(byte[] clientId, byte[] clientSecret)
    {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
    }

    internal ReadOnlySpan<byte> ClientId =>
        clientId ?? throw new ObjectDisposedException(nameof(PayPalSandboxCredentialLease));

    internal ReadOnlySpan<byte> ClientSecret =>
        clientSecret ?? throw new ObjectDisposedException(nameof(PayPalSandboxCredentialLease));

    public static PayPalSandboxCredentialLease Create(
        ReadOnlySpan<byte> clientId,
        ReadOnlySpan<byte> clientSecret)
    {
        ValidateAscii(clientId, 1, 256, permitColon: false, "client ID");
        ValidateAscii(clientSecret, 1, 512, permitColon: true, "client secret");
        return new PayPalSandboxCredentialLease(clientId.ToArray(), clientSecret.ToArray());
    }

    public void Dispose()
    {
        var id = Interlocked.Exchange(ref clientId, null);
        var secret = Interlocked.Exchange(ref clientSecret, null);
        if (id is not null) CryptographicOperations.ZeroMemory(id);
        if (secret is not null) CryptographicOperations.ZeroMemory(secret);
    }

    private static void ValidateAscii(
        ReadOnlySpan<byte> value,
        int minimum,
        int maximum,
        bool permitColon,
        string name)
    {
        if (value.Length < minimum || value.Length > maximum ||
            value.ContainsAnyExceptInRange((byte)0x21, (byte)0x7e) ||
            !permitColon && value.Contains((byte)':'))
        {
            throw new ArgumentException($"The PayPal sandbox {name} is invalid.");
        }
    }
}

public sealed record PayPalSandboxWebhookVerificationEvidence(
    string AuthAlgorithm,
    Uri CertificateUrl,
    string TransmissionId,
    string TransmissionSignature,
    string TransmissionTime);

public sealed class PayPalSandboxVerifiedWebhook
{
    internal PayPalSandboxVerifiedWebhook(
        string eventId,
        string transmissionId,
        string eventType,
        string providerObjectId,
        string? subscriptionId,
        string? planId,
        string? relatedSaleId,
        long amountCents,
        string currency,
        DateTimeOffset occurredUtc,
        string payloadSha256)
    {
        EventId = eventId;
        TransmissionId = transmissionId;
        EventType = eventType;
        ProviderObjectId = providerObjectId;
        SubscriptionId = subscriptionId;
        PlanId = planId;
        RelatedSaleId = relatedSaleId;
        AmountCents = amountCents;
        Currency = currency;
        OccurredUtc = occurredUtc;
        PayloadSha256 = payloadSha256;
    }

    public string EventId { get; }
    public string TransmissionId { get; }
    public string EventType { get; }
    public string ProviderObjectId { get; }
    public string? SubscriptionId { get; }
    public string? PlanId { get; }
    public string? RelatedSaleId { get; }
    public long AmountCents { get; }
    public string Currency { get; }
    public DateTimeOffset OccurredUtc { get; }
    public string PayloadSha256 { get; }
}

public sealed record PayPalSandboxRecipient(
    string SnapshotId,
    string ContributorPseudonym,
    string Email,
    bool IsVerifiedAndEligible);

public interface IPayPalSandboxRecipientDirectory
{
    ValueTask<PayPalSandboxRecipient?> ResolveAsync(
        string snapshotId,
        CancellationToken cancellationToken);
}

public interface IPayPalSandboxPayoutReferenceStore
{
    Task<string?> GetProviderBatchIdAsync(
        string localBatchId,
        CancellationToken cancellationToken = default);

    Task StoreProviderBatchIdAsync(
        string localBatchId,
        string providerBatchId,
        CancellationToken cancellationToken = default);
}

public static class PayPalSandboxValidation
{
    public static void RequireLocalBatchId(string value) =>
        RequireIdentifier(value, 128, "local payout batch");

    public static void RequireProviderBatchId(string value) =>
        RequireIdentifier(value, 30, "provider payout batch");

    internal static void RequireContributorPseudonym(string value) =>
        RequireIdentifier(value, 128, "contributor pseudonym");

    internal static void RequireRecipientSnapshotId(string value) =>
        RequireIdentifier(value, 128, "recipient snapshot");

    public static void RequireSubscriptionPlanId(string value) =>
        RequireIdentifier(value, 50, "subscription plan");

    private static void RequireIdentifier(string value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not (':' or '.' or '_' or '-')))
        {
            throw new PayPalSandboxException($"paypal_sandbox_{label.Replace(' ', '_')}_invalid");
        }
    }
}

public sealed class PayPalSandboxHttpAdapter : IReconcilablePayoutSandboxProvider, IDisposable
{
    public static readonly Uri SandboxOrigin = new("https://api-m.sandbox.paypal.com/");

    internal const int MaximumWebhookEventBytes = 512 * 1024;
    internal const int MaximumResponseBytes = 256 * 1024;
    internal const int MaximumPayoutRequestBytes = 512 * 1024;
    internal const int MaximumPayoutItems = 100;
    internal const long MaximumPayoutItemCents = 1_000_000;
    internal const long MaximumPayoutBatchCents = 10_000_000;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly HttpClient httpClient;
    private readonly IPayPalSandboxCredentialSource credentialSource;
    private readonly IPayPalSandboxRecipientDirectory recipientDirectory;
    private readonly IPayPalSandboxPayoutReferenceStore referenceStore;
    private readonly string webhookId;

    public PayPalSandboxHttpAdapter(
        IPayPalSandboxCredentialSource credentialSource,
        string webhookId,
        IPayPalSandboxRecipientDirectory recipientDirectory,
        IPayPalSandboxPayoutReferenceStore referenceStore,
        TimeSpan? timeout = null)
        : this(
            CreateHardenedHandler(), credentialSource, webhookId,
            recipientDirectory, referenceStore, timeout)
    {
    }

    internal PayPalSandboxHttpAdapter(
        HttpMessageHandler handler,
        IPayPalSandboxCredentialSource credentialSource,
        string webhookId,
        IPayPalSandboxRecipientDirectory recipientDirectory,
        IPayPalSandboxPayoutReferenceStore referenceStore,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.credentialSource = credentialSource ?? throw new ArgumentNullException(nameof(credentialSource));
        this.recipientDirectory = recipientDirectory ?? throw new ArgumentNullException(nameof(recipientDirectory));
        this.referenceStore = referenceStore ?? throw new ArgumentNullException(nameof(referenceStore));
        if (string.IsNullOrEmpty(webhookId) || webhookId.Length > 50 ||
            webhookId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_id_invalid");
        }

        this.webhookId = webhookId;

        var boundedTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (boundedTimeout < TimeSpan.FromSeconds(1) || boundedTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = SandboxOrigin,
            Timeout = boundedTimeout,
        };
    }

    public async Task<bool> VerifyWebhookAsync(
        PayPalSandboxWebhookVerificationEvidence evidence,
        ReadOnlyMemory<byte> webhookEventJson,
        CancellationToken cancellationToken = default)
    {
        ValidateWebhookEvidence(evidence);
        var webhookEvent = ParseWebhookEvent(webhookEventJson);
        return await VerifyParsedWebhookAsync(evidence, webhookEvent, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PayPalSandboxVerifiedWebhook> VerifyAndMapWebhookAsync(
        PayPalSandboxWebhookVerificationEvidence evidence,
        ReadOnlyMemory<byte> webhookEventJson,
        CancellationToken cancellationToken = default)
    {
        ValidateWebhookEvidence(evidence);
        var webhookEvent = ParseWebhookEvent(webhookEventJson);
        var mapped = MapSubscriptionWebhook(evidence.TransmissionId, webhookEvent);
        if (!await VerifyParsedWebhookAsync(evidence, webhookEvent, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_not_verified");
        }

        return mapped;
    }

    private async Task<bool> VerifyParsedWebhookAsync(
        PayPalSandboxWebhookVerificationEvidence evidence,
        JsonElement webhookEvent,
        CancellationToken cancellationToken)
    {

        var body = WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("auth_algo", evidence.AuthAlgorithm);
            writer.WriteString("cert_url", evidence.CertificateUrl.AbsoluteUri);
            writer.WriteString("transmission_id", evidence.TransmissionId);
            writer.WriteString("transmission_sig", evidence.TransmissionSignature);
            writer.WriteString("transmission_time", evidence.TransmissionTime);
            writer.WriteString("webhook_id", webhookId);
            writer.WritePropertyName("webhook_event");
            webhookEvent.WriteTo(writer);
            writer.WriteEndObject();
        }, MaximumWebhookEventBytes + 16 * 1024, "paypal_sandbox_webhook_event_invalid");

        byte[]? responseBody = null;
        try
        {
            var token = await AcquireAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAuthorizedAsync(
                    HttpMethod.Post,
                    new Uri(SandboxOrigin, "v1/notifications/verify-webhook-signature"),
                    token,
                    body,
                    requestId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new PayPalSandboxException("paypal_sandbox_webhook_verification_failed");
            }

            responseBody = await ReadJsonBodyAsync(response, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            using var document = ParseResponse(responseBody, "paypal_sandbox_webhook_response_invalid");
            var root = RequireObject(document.RootElement, "paypal_sandbox_webhook_response_invalid");
            var status = RequireString(root, "verification_status", 16,
                "paypal_sandbox_webhook_response_invalid");
            return status switch
            {
                "SUCCESS" => true,
                "FAILURE" => false,
                _ => throw new PayPalSandboxException("paypal_sandbox_webhook_response_invalid"),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if (responseBody is not null) CryptographicOperations.ZeroMemory(responseBody);
        }
    }

    public async Task<PayoutBatchResult> SendSandboxAsync(
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        ValidatePreview(preview);
        var existingReference = await GetReferenceAsync(preview.BatchId, cancellationToken)
            .ConfigureAwait(false);
        if (existingReference is not null)
        {
            var lookup = await LookupWithProviderReferenceAsync(
                    preview, existingReference, cancellationToken)
                .ConfigureAwait(false);
            return ResultFromLookup(lookup);
        }

        var recipients = await ResolveRecipientsAsync(preview, cancellationToken).ConfigureAwait(false);
        var requestBody = WritePayoutRequest(preview, recipients);
        byte[]? responseBody = null;
        try
        {
            var token = await AcquireAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await SendAuthorizedAsync(
                    HttpMethod.Post,
                    new Uri(SandboxOrigin, "v1/payments/payouts"),
                    token,
                    requestBody,
                    StableRequestId(preview.BatchId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new PayPalSandboxException("paypal_sandbox_payout_submission_failed");
            }

            responseBody = await ReadJsonBodyAsync(response, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            var parsed = ParsePayoutResponse(
                responseBody, preview.BatchId, "paypal_sandbox_payout_response_invalid");
            await StoreReferenceAsync(preview.BatchId, parsed.ProviderBatchId, cancellationToken)
                .ConfigureAwait(false);
            return ResultFromSubmissionStatus(preview.BatchId, parsed.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBody);
            if (responseBody is not null) CryptographicOperations.ZeroMemory(responseBody);
        }
    }

    public async Task<SandboxPayoutLookupResult> LookupSandboxAsync(
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        ValidatePreview(preview);
        var providerBatchId = await GetReferenceAsync(preview.BatchId, cancellationToken)
            .ConfigureAwait(false);
        return providerBatchId is null
            ? new SandboxPayoutLookupResult(
                preview.BatchId, SandboxPayoutLookupState.NotFound, null)
            : await LookupWithProviderReferenceAsync(preview, providerBatchId, cancellationToken)
                .ConfigureAwait(false);
    }

    public void Dispose() => httpClient.Dispose();

    internal static SocketsHttpHandler CreateHardenedHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        UseProxy = false,
    };

    private async Task<SandboxPayoutLookupResult> LookupWithProviderReferenceAsync(
        PayoutBatchPreview preview,
        string providerBatchId,
        CancellationToken cancellationToken)
    {
        PayPalSandboxValidation.RequireProviderBatchId(providerBatchId);
        byte[]? responseBody = null;
        var token = await AcquireAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAuthorizedAsync(
                HttpMethod.Get,
                new Uri(SandboxOrigin,
                    $"v1/payments/payouts/{Uri.EscapeDataString(providerBatchId)}" +
                    "?fields=items&page=1&page_size=100&total_required=true"),
                token,
                body: null,
                requestId: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PayPalSandboxException("paypal_sandbox_provider_reference_missing");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PayPalSandboxException("paypal_sandbox_payout_lookup_failed");
        }

        try
        {
            responseBody = await ReadJsonBodyAsync(response, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            var parsed = ParsePayoutLookupResponse(
                responseBody, preview, providerBatchId,
                "paypal_sandbox_payout_lookup_response_invalid");
            if (!string.Equals(parsed.ProviderBatchId, providerBatchId, StringComparison.Ordinal))
            {
                throw new PayPalSandboxException("paypal_sandbox_payout_lookup_response_invalid");
            }

            return LookupFromStatus(preview.BatchId, parsed);
        }
        finally
        {
            if (responseBody is not null) CryptographicOperations.ZeroMemory(responseBody);
        }
    }

    private async Task<string> AcquireAccessTokenAsync(CancellationToken cancellationToken)
    {
        PayPalSandboxCredentialLease? credentials = null;
        byte[]? combined = null;
        byte[]? responseBody = null;
        try
        {
            try
            {
                credentials = await credentialSource.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new PayPalSandboxException("paypal_sandbox_credentials_unavailable");
            }

            if (credentials is null)
            {
                throw new PayPalSandboxException("paypal_sandbox_credentials_unavailable");
            }

            combined = new byte[credentials.ClientId.Length + 1 + credentials.ClientSecret.Length];
            credentials.ClientId.CopyTo(combined);
            combined[credentials.ClientId.Length] = (byte)':';
            credentials.ClientSecret.CopyTo(combined.AsSpan(credentials.ClientId.Length + 1));

            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(SandboxOrigin, "v1/oauth2/token"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(combined));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                "grant_type=client_credentials", Encoding.ASCII, "application/x-www-form-urlencoded");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new PayPalSandboxException("paypal_sandbox_oauth_failed");
            }

            responseBody = await ReadJsonBodyAsync(response, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            using var document = ParseResponse(responseBody, "paypal_sandbox_oauth_response_invalid");
            var root = RequireObject(document.RootElement, "paypal_sandbox_oauth_response_invalid");
            var tokenType = RequireString(root, "token_type", 32,
                "paypal_sandbox_oauth_response_invalid");
            var token = RequireString(root, "access_token", 4096,
                "paypal_sandbox_oauth_response_invalid");
            if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                token.Any(character => character is <= ' ' or > '~'))
            {
                throw new PayPalSandboxException("paypal_sandbox_oauth_response_invalid");
            }

            if (!root.TryGetProperty("expires_in", out var expires) ||
                expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) ||
                seconds is < 1 or > 86_400)
            {
                throw new PayPalSandboxException("paypal_sandbox_oauth_response_invalid");
            }

            return token;
        }
        finally
        {
            credentials?.Dispose();
            if (combined is not null) CryptographicOperations.ZeroMemory(combined);
            if (responseBody is not null) CryptographicOperations.ZeroMemory(responseBody);
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        Uri uri,
        string token,
        byte[]? body,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (requestId is not null) request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        try
        {
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ValidateSandboxUri(request.RequestUri);
        try
        {
            var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is not { } responseUri ||
                !Uri.Equals(responseUri, request.RequestUri))
            {
                response.Dispose();
                throw new PayPalSandboxException("paypal_sandbox_response_origin_invalid");
            }

            return response;
        }
        catch (PayPalSandboxException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new PayPalSandboxException("paypal_sandbox_transport_failed");
        }
    }

    private static void ValidateSandboxUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !string.Equals(uri.IdnHost, SandboxOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new PayPalSandboxException("paypal_sandbox_request_origin_invalid");
        }
    }

    private async Task<IReadOnlyList<PayPalSandboxRecipient>> ResolveRecipientsAsync(
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        var recipients = new List<PayPalSandboxRecipient>(preview.Items.Count);
        var uniqueEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in preview.Items)
        {
            PayPalSandboxRecipient? recipient;
            try
            {
                recipient = await recipientDirectory.ResolveAsync(
                        item.RecipientApproval.SnapshotId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new PayPalSandboxException("paypal_sandbox_recipient_directory_failed");
            }

            if (recipient is null || !recipient.IsVerifiedAndEligible ||
                !string.Equals(
                    recipient.SnapshotId,
                    item.RecipientApproval.SnapshotId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    recipient.ContributorPseudonym,
                    item.ContributorPseudonym,
                    StringComparison.Ordinal) ||
                !IsValidEmail(recipient.Email) ||
                !string.Equals(
                    DestinationSha256(recipient.Email),
                    item.RecipientApproval.DestinationSha256,
                    StringComparison.Ordinal) ||
                !uniqueEmails.Add(recipient.Email))
            {
                throw new PayPalSandboxException("paypal_sandbox_recipient_snapshot_invalid");
            }

            recipients.Add(recipient);
        }

        return recipients;
    }

    private async Task<string?> GetReferenceAsync(string localBatchId, CancellationToken cancellationToken)
    {
        try
        {
            var value = await referenceStore.GetProviderBatchIdAsync(localBatchId, cancellationToken)
                .ConfigureAwait(false);
            if (value is not null) PayPalSandboxValidation.RequireProviderBatchId(value);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PayPalSandboxException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PayPalSandboxException("paypal_sandbox_reference_store_failed");
        }
    }

    private async Task StoreReferenceAsync(
        string localBatchId,
        string providerBatchId,
        CancellationToken cancellationToken)
    {
        try
        {
            await referenceStore.StoreProviderBatchIdAsync(
                    localBatchId, providerBatchId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PayPalSandboxException("paypal_sandbox_reference_store_failed");
        }
    }

    private static byte[] WritePayoutRequest(
        PayoutBatchPreview preview,
        IReadOnlyList<PayPalSandboxRecipient> recipients) =>
        WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("sender_batch_header");
            writer.WriteStartObject();
            writer.WriteString("sender_batch_id", preview.BatchId);
            writer.WriteString("recipient_type", "EMAIL");
            writer.WriteEndObject();
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            for (var index = 0; index < preview.Items.Count; index++)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("amount");
                writer.WriteStartObject();
                writer.WriteString("currency", "USD");
                writer.WriteString("value", Cents(preview.Items[index].AmountCents));
                writer.WriteEndObject();
                writer.WriteString("receiver", recipients[index].Email);
                writer.WriteString("sender_item_id", $"dcx-{index + 1:D6}");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, MaximumPayoutRequestBytes, "paypal_sandbox_payout_request_oversized");

    private static byte[] WriteJson(Action<Utf8JsonWriter> write, int maximumBytes, string errorCode)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false,
                   MaxDepth = 32,
               }))
        {
            write(writer);
        }

        if (output.Length > maximumBytes) throw new PayPalSandboxException(errorCode);
        var result = output.ToArray();
        if (output.TryGetBuffer(out var segment))
        {
            CryptographicOperations.ZeroMemory(segment.AsSpan(0, (int)output.Length));
        }

        return result;
    }

    private static PayoutResponse ParsePayoutResponse(
        ReadOnlyMemory<byte> body,
        string expectedLocalBatchId,
        string errorCode)
    {
        using var document = ParseResponse(body, errorCode);
        var root = RequireObject(document.RootElement, errorCode);
        return ParsePayoutHeader(root, expectedLocalBatchId, errorCode);
    }

    private static PayoutResponse ParsePayoutLookupResponse(
        ReadOnlyMemory<byte> body,
        PayoutBatchPreview preview,
        string expectedProviderBatchId,
        string errorCode)
    {
        using var document = ParseResponse(body, errorCode);
        var root = RequireObject(document.RootElement, errorCode);
        var response = ParsePayoutHeader(root, preview.BatchId, errorCode);
        if (!string.Equals(response.ProviderBatchId, expectedProviderBatchId, StringComparison.Ordinal) ||
            response.Status != "SUCCESS")
        {
            return response;
        }

        if (!root.TryGetProperty("total_items", out var totalItems) ||
            totalItems.ValueKind != JsonValueKind.Number ||
            !totalItems.TryGetInt32(out var totalItemCount) || totalItemCount != preview.Items.Count ||
            !root.TryGetProperty("total_pages", out var totalPages) ||
            totalPages.ValueKind != JsonValueKind.Number ||
            !totalPages.TryGetInt32(out var totalPageCount) || totalPageCount != 1 ||
            !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() != preview.Items.Count)
        {
            throw new PayPalSandboxException(errorCode);
        }

        var expectedItems = preview.Items
            .Select((item, index) => new KeyValuePair<string, ExpectedPayoutItem>(
                $"dcx-{index + 1:D6}", new ExpectedPayoutItem(index, item)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var itemResults = new PayoutItemResult[preview.Items.Count];
        var hasPendingItem = false;
        var seenSenderItems = new HashSet<string>(StringComparer.Ordinal);
        var seenProviderItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in items.EnumerateArray())
        {
            var item = RequireObject(value, errorCode);
            var providerItemId = RequireString(item, "payout_item_id", 30, errorCode);
            var providerBatchId = RequireString(item, "payout_batch_id", 30, errorCode);
            var transactionStatus = RequireString(item, "transaction_status", 36, errorCode);
            if (!seenProviderItems.Add(providerItemId) ||
                !string.Equals(providerBatchId, expectedProviderBatchId, StringComparison.Ordinal))
            {
                throw new PayPalSandboxException(errorCode);
            }

            RequireTransactionStatus(transactionStatus, errorCode);
            var detail = RequireObject(RequireProperty(item, "payout_item", errorCode), errorCode);
            var senderItemId = RequireString(detail, "sender_item_id", 63, errorCode);
            var receiver = RequireString(detail, "receiver", 127, errorCode);
            var amount = RequireObject(RequireProperty(detail, "amount", errorCode), errorCode);
            var currency = RequireString(amount, "currency", 3, errorCode);
            var amountValue = RequireString(amount, "value", 64, errorCode);
            if (!expectedItems.TryGetValue(senderItemId, out var expected) ||
                !seenSenderItems.Add(senderItemId) || !IsValidEmail(receiver) ||
                !string.Equals(currency, "USD", StringComparison.Ordinal) ||
                !TryParseUsdCents(amountValue, out var actualCents) ||
                actualCents != expected.Item.AmountCents ||
                !string.Equals(
                    DestinationSha256(receiver),
                    expected.Item.RecipientApproval.DestinationSha256,
                    StringComparison.Ordinal))
            {
                throw new PayPalSandboxException(errorCode);
            }

            var succeeded = transactionStatus == "SUCCESS";
            hasPendingItem |= transactionStatus is "PENDING" or "UNCLAIMED" or "ONHOLD";
            itemResults[expected.Index] = new PayoutItemResult(
                expected.Item.ContributorPseudonym,
                expected.Item.AmountCents,
                succeeded,
                transactionStatus);
        }

        if (seenSenderItems.Count != expectedItems.Count)
        {
            throw new PayPalSandboxException(errorCode);
        }

        return response with
        {
            ItemResults = itemResults,
            HasPendingItem = hasPendingItem,
        };
    }

    private static PayoutResponse ParsePayoutHeader(
        JsonElement root,
        string expectedLocalBatchId,
        string errorCode)
    {
        var batch = RequireObject(RequireProperty(root, "batch_header", errorCode), errorCode);
        var providerBatchId = RequireString(batch, "payout_batch_id", 30, errorCode);
        var status = RequireString(batch, "batch_status", 16, errorCode);
        var sender = RequireObject(RequireProperty(batch, "sender_batch_header", errorCode), errorCode);
        var senderBatchId = RequireString(sender, "sender_batch_id", 128, errorCode);
        PayPalSandboxValidation.RequireProviderBatchId(providerBatchId);
        RequireBatchStatus(status, errorCode);
        if (!string.Equals(senderBatchId, expectedLocalBatchId, StringComparison.Ordinal))
        {
            throw new PayPalSandboxException(errorCode);
        }

        return new PayoutResponse(providerBatchId, status);
    }

    private static bool TryParseUsdCents(string value, out long cents)
    {
        cents = 0;
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount) || amount <= 0 || decimal.Truncate(amount * 100m) != amount * 100m)
        {
            return false;
        }

        try
        {
            cents = decimal.ToInt64(amount * 100m);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static PayoutBatchResult ResultFromLookup(SandboxPayoutLookupResult lookup) =>
        lookup.State switch
        {
            SandboxPayoutLookupState.Succeeded =>
                new PayoutBatchResult(
                    lookup.BatchId,
                    PayoutBatchOutcome.Succeeded,
                    lookup.ProviderStatus!,
                    lookup.ItemResults),
            SandboxPayoutLookupState.Failed =>
                new PayoutBatchResult(
                    lookup.BatchId,
                    PayoutBatchOutcome.Failed,
                    lookup.ProviderStatus!,
                    lookup.ItemResults),
            SandboxPayoutLookupState.PartiallySucceeded =>
                new PayoutBatchResult(
                    lookup.BatchId,
                    PayoutBatchOutcome.PartiallySucceeded,
                    lookup.ProviderStatus!,
                    lookup.ItemResults),
            SandboxPayoutLookupState.Pending =>
                new PayoutBatchResult(
                    lookup.BatchId,
                    PayoutBatchOutcome.Pending,
                    lookup.ProviderStatus!),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_lookup_state_invalid"),
        };

    private static PayoutBatchResult ResultFromSubmissionStatus(string localBatchId, string status) =>
        status switch
        {
            "SUCCESS" => new PayoutBatchResult(
                localBatchId,
                PayoutBatchOutcome.Pending,
                "SUCCESS_AWAITING_ITEM_RECONCILIATION"),
            "DENIED" or "CANCELED" => new PayoutBatchResult(
                localBatchId, PayoutBatchOutcome.Failed, status),
            "PENDING" or "PROCESSING" => new PayoutBatchResult(
                localBatchId, PayoutBatchOutcome.Pending, status),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_status_invalid"),
        };

    private static SandboxPayoutLookupResult LookupFromStatus(
        string localBatchId,
        PayoutResponse response)
    {
        if (response.Status == "SUCCESS" && response.ItemResults is { Count: > 0 } itemResults)
        {
            if (response.HasPendingItem)
            {
                return new SandboxPayoutLookupResult(
                    localBatchId,
                    SandboxPayoutLookupState.Pending,
                    "SUCCESS_WITH_PENDING_ITEMS");
            }

            var succeeded = itemResults.Count(item => item.Succeeded);
            var state = succeeded switch
            {
                0 => SandboxPayoutLookupState.Failed,
                var count when count == itemResults.Count => SandboxPayoutLookupState.Succeeded,
                _ => SandboxPayoutLookupState.PartiallySucceeded,
            };
            return new SandboxPayoutLookupResult(
                localBatchId,
                state,
                response.Status,
                itemResults);
        }

        return response.Status switch
        {
            "DENIED" or "CANCELED" => new SandboxPayoutLookupResult(
                localBatchId, SandboxPayoutLookupState.Failed, response.Status),
            "PENDING" or "PROCESSING" => new SandboxPayoutLookupResult(
                localBatchId, SandboxPayoutLookupState.Pending, response.Status),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_status_invalid"),
        };
    }

    private static void ValidatePreview(PayoutBatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        PayPalSandboxValidation.RequireLocalBatchId(preview.BatchId);
        if (preview.Mode != PayoutExecutionMode.Sandbox || preview.Items is null ||
            preview.Items.Count is < 1 or > MaximumPayoutItems)
        {
            throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
        }

        long total = 0;
        var pseudonyms = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in preview.Items)
            {
                if (item is null)
                {
                    throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
                }
                PayPalSandboxValidation.RequireContributorPseudonym(item.ContributorPseudonym);
                if (item.RecipientApproval is null)
                {
                    throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
                }
                PayPalSandboxValidation.RequireRecipientSnapshotId(
                    item.RecipientApproval.SnapshotId);
                if (item.AmountCents is < 1 or > MaximumPayoutItemCents ||
                    !PayoutContractValidation.IsLowerSha256(
                        item.RecipientApproval.DestinationSha256) ||
                    !pseudonyms.Add(item.ContributorPseudonym) ||
                    !snapshots.Add(item.RecipientApproval.SnapshotId))
                {
                    throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
                }

                total = checked(total + item.AmountCents);
            }
        }
        catch (OverflowException)
        {
            throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
        }

        if (total > MaximumPayoutBatchCents)
        {
            throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
        }
    }

    private static void ValidateWebhookEvidence(PayPalSandboxWebhookVerificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireIdentifier(evidence.TransmissionId, 50, "transmission ID");
        if (!string.Equals(evidence.AuthAlgorithm, "SHA256withRSA", StringComparison.Ordinal) ||
            evidence.TransmissionSignature.Length is < 40 or > 500 ||
            !TryValidateBase64(evidence.TransmissionSignature) ||
            evidence.TransmissionTime.Length is < 20 or > 100 ||
            !HasRfc3339Offset(evidence.TransmissionTime) ||
            !DateTimeOffset.TryParse(
                evidence.TransmissionTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out _) ||
            evidence.CertificateUrl.AbsoluteUri.Length > 500 ||
            !IsApprovedCertificateUri(evidence.CertificateUrl))
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_evidence_invalid");
        }
    }

    private static JsonElement ParseWebhookEvent(ReadOnlyMemory<byte> webhookEventJson)
    {
        if (webhookEventJson.IsEmpty || webhookEventJson.Length > MaximumWebhookEventBytes)
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
        }

        try
        {
            using var eventDocument = JsonDocument.Parse(webhookEventJson, DocumentOptions);
            RequireNoDuplicateProperties(eventDocument.RootElement);
            if (eventDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
            }

            return eventDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
        }
    }

    private static PayPalSandboxVerifiedWebhook MapSubscriptionWebhook(
        string transmissionId,
        JsonElement webhookEvent)
    {
        const string errorCode = "paypal_sandbox_webhook_resource_invalid";
        var eventId = RequireString(webhookEvent, "id", 128, errorCode);
        RequireIdentifier(eventId, 128, "webhook event");
        var eventType = RequireString(webhookEvent, "event_type", 50, errorCode);
        if (eventType is not ("BILLING.SUBSCRIPTION.ACTIVATED" or
            "BILLING.SUBSCRIPTION.CANCELLED" or "BILLING.SUBSCRIPTION.SUSPENDED" or
            "BILLING.SUBSCRIPTION.EXPIRED" or "PAYMENT.SALE.COMPLETED" or
            "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED"))
        {
            throw new PayPalSandboxException(errorCode);
        }

        var occurredText = RequireString(webhookEvent, "create_time", 100, errorCode);
        if (!HasRfc3339Offset(occurredText) ||
            !DateTimeOffset.TryParse(
                occurredText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var occurredUtc))
        {
            throw new PayPalSandboxException(errorCode);
        }

        var resourceType = RequireString(webhookEvent, "resource_type", 64, errorCode);
        var resource = RequireObject(RequireProperty(webhookEvent, "resource", errorCode), errorCode);
        var providerObjectId = RequireString(resource, "id", 128, errorCode);
        RequireIdentifier(providerObjectId, 128, "provider object");

        string? subscriptionId = null;
        string? planId = null;
        string? relatedSaleId = null;
        long amountCents = 0;
        var currency = "USD";

        if (eventType.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal))
        {
            if (!string.Equals(resourceType, "subscription", StringComparison.Ordinal))
            {
                throw new PayPalSandboxException(errorCode);
            }

            subscriptionId = providerObjectId;
            RequireIdentifier(subscriptionId, 50, "subscription");
            planId = RequireString(resource, "plan_id", 50, errorCode);
            RequireIdentifier(planId, 50, "subscription plan");
            var status = RequireString(resource, "status", 32, errorCode);
            var expectedStatus = eventType switch
            {
                "BILLING.SUBSCRIPTION.ACTIVATED" => "ACTIVE",
                "BILLING.SUBSCRIPTION.CANCELLED" => "CANCELLED",
                "BILLING.SUBSCRIPTION.SUSPENDED" => "SUSPENDED",
                "BILLING.SUBSCRIPTION.EXPIRED" => "EXPIRED",
                _ => throw new PayPalSandboxException(errorCode),
            };
            if (!string.Equals(status, expectedStatus, StringComparison.Ordinal))
            {
                throw new PayPalSandboxException(errorCode);
            }
        }
        else
        {
            if (resourceType is not ("sale" or "refund"))
            {
                throw new PayPalSandboxException(errorCode);
            }

            var amount = RequireObject(RequireProperty(resource, "amount", errorCode), errorCode);
            currency = RequireString(amount, "currency", 3, errorCode);
            var total = RequireString(amount, "total", 16, errorCode);
            amountCents = ParseUsdCents(total, errorCode);
            var state = RequireString(resource, "state", 32, errorCode);

            if (resource.TryGetProperty("billing_agreement_id", out var agreementProperty))
            {
                if (agreementProperty.ValueKind != JsonValueKind.String)
                {
                    throw new PayPalSandboxException(errorCode);
                }

                subscriptionId = agreementProperty.GetString();
                if (string.IsNullOrEmpty(subscriptionId))
                {
                    throw new PayPalSandboxException(errorCode);
                }

                RequireIdentifier(subscriptionId, 50, "subscription");
            }

            if (eventType == "PAYMENT.SALE.COMPLETED")
            {
                if (!string.Equals(resourceType, "sale", StringComparison.Ordinal) ||
                    !string.Equals(state, "completed", StringComparison.Ordinal) ||
                    subscriptionId is null || amountCents != PayPalSandboxWebhookProcessor.MonthlyPlanCents)
                {
                    throw new PayPalSandboxException(errorCode);
                }

                relatedSaleId = providerObjectId;
            }
            else
            {
                if ((eventType == "PAYMENT.SALE.REFUNDED" &&
                     state is not ("refunded" or "partially_refunded" or "completed")) ||
                    (eventType == "PAYMENT.SALE.REVERSED" &&
                     state is not ("reversed" or "completed")))
                {
                    throw new PayPalSandboxException(errorCode);
                }

                if (resource.TryGetProperty("sale_id", out var saleProperty))
                {
                    if (saleProperty.ValueKind != JsonValueKind.String)
                    {
                        throw new PayPalSandboxException(errorCode);
                    }

                    relatedSaleId = saleProperty.GetString();
                    if (string.IsNullOrEmpty(relatedSaleId))
                    {
                        throw new PayPalSandboxException(errorCode);
                    }

                    RequireIdentifier(relatedSaleId, 128, "related sale");
                }
                else if (string.Equals(resourceType, "sale", StringComparison.Ordinal))
                {
                    relatedSaleId = providerObjectId;
                }

                if (relatedSaleId is null || amountCents is < 1 or > PayPalSandboxWebhookProcessor.MonthlyPlanCents)
                {
                    throw new PayPalSandboxException(errorCode);
                }
            }
        }

        byte[]? canonicalPayload = null;
        try
        {
            canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(webhookEvent);
            var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(canonicalPayload));
            return new PayPalSandboxVerifiedWebhook(
                eventId,
                transmissionId,
                eventType,
                providerObjectId,
                subscriptionId,
                planId,
                relatedSaleId,
                amountCents,
                currency,
                occurredUtc.ToUniversalTime(),
                payloadSha256);
        }
        finally
        {
            if (canonicalPayload is not null) CryptographicOperations.ZeroMemory(canonicalPayload);
        }
    }

    private static long ParseUsdCents(string value, string errorCode)
    {
        if (value.Length is < 4 or > 10 || value[^3] != '.' ||
            !value.AsSpan(0, value.Length - 3).ContainsAnyInRange('0', '9') ||
            value.AsSpan().ContainsAnyExcept("0123456789."))
        {
            throw new PayPalSandboxException(errorCode);
        }

        var decimalIndex = value.Length - 3;
        if ((decimalIndex > 1 && value[0] == '0') ||
            !value.AsSpan(0, decimalIndex).ToString().All(char.IsAsciiDigit) ||
            !char.IsAsciiDigit(value[^2]) || !char.IsAsciiDigit(value[^1]) ||
            !decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new PayPalSandboxException(errorCode);
        }

        try
        {
            return checked((long)(parsed * 100m));
        }
        catch (OverflowException)
        {
            throw new PayPalSandboxException(errorCode);
        }
    }

    private static bool TryValidateBase64(string value)
    {
        var maximumBytes = value.Length;
        var decoded = new byte[maximumBytes];
        try
        {
            return Convert.TryFromBase64String(value, decoded, out var written) && written >= 32;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static bool HasRfc3339Offset(string value) =>
        value.EndsWith('Z') ||
        value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-';

    private static bool IsApprovedCertificateUri(Uri uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "https", Port: 443 } &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.StartsWith("/v1/notifications/certs/", StringComparison.Ordinal) &&
        uri.IdnHost is "api.paypal.com" or "api-m.paypal.com" or
            "api.sandbox.paypal.com" or "api-m.sandbox.paypal.com";

    private static bool IsValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 127 ||
            value.Any(character => character is <= ' ' or > '~')) return false;
        var at = value.IndexOf('@');
        if (at is < 1 || at != value.LastIndexOf('@') || at > 64 || at == value.Length - 1) return false;
        var local = value.AsSpan(0, at);
        var domain = value.AsSpan(at + 1);
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal) ||
            !local.ToString().All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '%' or '+' or '-')) return false;
        if (domain[0] == '.' || domain[^1] == '.' || !domain.Contains('.')) return false;
        return domain.ToString().Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static string StableRequestId(string batchId)
    {
        var bytes = Encoding.UTF8.GetBytes(batchId);
        try
        {
            return $"dcx-{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Cents(long value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value / 100}.{value % 100:D2}");

    private static string DestinationSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<byte[]> ReadJsonBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PayPalSandboxException("paypal_sandbox_response_media_type_invalid");
        }

        if (response.Content.Headers.ContentLength is long length &&
            (length < 0 || length > maximumBytes))
        {
            throw new PayPalSandboxException("paypal_sandbox_response_oversized");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                {
                    throw new PayPalSandboxException("paypal_sandbox_response_oversized");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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

    private static JsonDocument ParseResponse(ReadOnlyMemory<byte> body, string errorCode)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(body, DocumentOptions);
            RequireNoDuplicateProperties(document.RootElement);
            return document;
        }
        catch (JsonException)
        {
            document?.Dispose();
            throw new PayPalSandboxException(errorCode);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void RequireNoDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new PayPalSandboxException("paypal_sandbox_json_duplicate_property");
                    }

                    RequireNoDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RequireNoDuplicateProperties(item);
                break;
        }
    }

    private static JsonElement RequireObject(JsonElement element, string errorCode)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new PayPalSandboxException(errorCode);
        return element;
    }

    private static JsonElement RequireProperty(JsonElement element, string name, string errorCode)
    {
        if (!element.TryGetProperty(name, out var property)) throw new PayPalSandboxException(errorCode);
        return property;
    }

    private static string RequireString(
        JsonElement element,
        string name,
        int maximumLength,
        string errorCode)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new PayPalSandboxException(errorCode);
        }

        var value = property.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new PayPalSandboxException(errorCode);
        }

        return value;
    }

    private static void RequireBatchStatus(string status, string errorCode)
    {
        if (status is not ("DENIED" or "PENDING" or "PROCESSING" or "SUCCESS" or "CANCELED"))
        {
            throw new PayPalSandboxException(errorCode);
        }
    }

    private static void RequireTransactionStatus(string status, string errorCode)
    {
        if (status is not ("SUCCESS" or "FAILED" or "PENDING" or "UNCLAIMED" or
            "RETURNED" or "ONHOLD" or "BLOCKED" or "REFUNDED" or "REVERSED"))
        {
            throw new PayPalSandboxException(errorCode);
        }
    }

    private static void RequireIdentifier(string value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not (':' or '.' or '_' or '-')))
        {
            throw new PayPalSandboxException(
                $"paypal_sandbox_{label.Replace(' ', '_').ToLowerInvariant()}_invalid");
        }
    }

    private sealed record ExpectedPayoutItem(int Index, PayoutItem Item);

    private sealed record PayoutResponse(
        string ProviderBatchId,
        string Status,
        IReadOnlyList<PayoutItemResult>? ItemResults = null,
        bool HasPendingItem = false);
}
