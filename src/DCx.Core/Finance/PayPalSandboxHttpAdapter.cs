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

public sealed record PayPalSandboxRecipient(string Email, bool IsVerifiedAndEligible);

public interface IPayPalSandboxRecipientDirectory
{
    ValueTask<PayPalSandboxRecipient?> ResolveAsync(
        string contributorPseudonym,
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
        if (webhookEventJson.IsEmpty || webhookEventJson.Length > MaximumWebhookEventBytes)
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
        }

        JsonElement webhookEvent;
        try
        {
            using var eventDocument = JsonDocument.Parse(webhookEventJson, DocumentOptions);
            RequireNoDuplicateProperties(eventDocument.RootElement);
            if (eventDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
            }

            webhookEvent = eventDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_event_invalid");
        }

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

            return LookupFromStatus(preview.BatchId, parsed.Status);
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
                        item.ContributorPseudonym, cancellationToken)
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
                !IsValidEmail(recipient.Email) || !uniqueEmails.Add(recipient.Email))
            {
                throw new PayPalSandboxException("paypal_sandbox_recipient_ineligible");
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
            .Select((item, index) => new KeyValuePair<string, long>(
                $"dcx-{index + 1:D6}", item.AmountCents))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
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
            if (!expectedItems.TryGetValue(senderItemId, out var expectedCents) ||
                !seenSenderItems.Add(senderItemId) || !IsValidEmail(receiver) ||
                !string.Equals(currency, "USD", StringComparison.Ordinal) ||
                !TryParseUsdCents(amountValue, out var actualCents) || actualCents != expectedCents)
            {
                throw new PayPalSandboxException(errorCode);
            }

            if (transactionStatus != "SUCCESS")
            {
                throw new PayPalSandboxException(
                    "paypal_sandbox_partial_payout_requires_item_reconciliation");
            }
        }

        if (seenSenderItems.Count != expectedItems.Count)
        {
            throw new PayPalSandboxException(errorCode);
        }

        return response;
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
                new PayoutBatchResult(lookup.BatchId, true, lookup.ProviderStatus!, true),
            SandboxPayoutLookupState.Failed =>
                new PayoutBatchResult(lookup.BatchId, false, lookup.ProviderStatus!, true),
            SandboxPayoutLookupState.Pending =>
                new PayoutBatchResult(lookup.BatchId, false, lookup.ProviderStatus!, false),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_lookup_state_invalid"),
        };

    private static PayoutBatchResult ResultFromSubmissionStatus(string localBatchId, string status) =>
        status switch
        {
            "SUCCESS" => new PayoutBatchResult(
                localBatchId, false, "SUCCESS_AWAITING_ITEM_RECONCILIATION", false),
            "DENIED" or "CANCELED" => new PayoutBatchResult(localBatchId, false, status, true),
            "PENDING" or "PROCESSING" => new PayoutBatchResult(localBatchId, false, status, false),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_status_invalid"),
        };

    private static SandboxPayoutLookupResult LookupFromStatus(string localBatchId, string status) =>
        status switch
        {
            "SUCCESS" => new SandboxPayoutLookupResult(
                localBatchId, SandboxPayoutLookupState.Succeeded, status),
            "DENIED" or "CANCELED" => new SandboxPayoutLookupResult(
                localBatchId, SandboxPayoutLookupState.Failed, status),
            "PENDING" or "PROCESSING" => new SandboxPayoutLookupResult(
                localBatchId, SandboxPayoutLookupState.Pending, status),
            _ => throw new PayPalSandboxException("paypal_sandbox_payout_status_invalid"),
        };

    private static void ValidatePreview(PayoutBatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        PayPalSandboxValidation.RequireLocalBatchId(preview.BatchId);
        if (preview.Mode != PayoutExecutionMode.Sandbox || preview.Items.Count is < 1 or > MaximumPayoutItems)
        {
            throw new PayPalSandboxException("paypal_sandbox_payout_preview_invalid");
        }

        long total = 0;
        var pseudonyms = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in preview.Items)
            {
                PayPalSandboxValidation.RequireContributorPseudonym(item.ContributorPseudonym);
                if (item.AmountCents is < 1 or > MaximumPayoutItemCents ||
                    !pseudonyms.Add(item.ContributorPseudonym))
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

    private sealed record PayoutResponse(string ProviderBatchId, string Status);
}
