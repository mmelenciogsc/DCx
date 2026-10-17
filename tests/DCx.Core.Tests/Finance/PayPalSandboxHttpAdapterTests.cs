using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DCx.Core.Finance;
using DCx.GlobalService;

namespace DCx.Core.Tests.Finance;

public sealed class PayPalSandboxHttpAdapterTests
{
    private const string ClientId = "sandbox-client-id";
    private const string FixtureCredentialPassword = "sandbox-client-secret-never-log";
    private const string AccessToken = "sandbox-access-token-never-log";

    [Fact]
    public async Task VerifyWebhook_UsesOfficialSandboxContractAndExactEventObject()
    {
        var calls = 0;
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            calls++;
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://api-m.sandbox.paypal.com/v1/notifications/verify-webhook-signature",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(AccessToken, request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var document = JsonDocument.Parse(
                await request.Content.ReadAsByteArrayAsync(cancellationToken));
            var root = document.RootElement;
            Assert.Equal("SHA256withRSA", root.GetProperty("auth_algo").GetString());
            Assert.Equal("WHDCXSANDBOX", root.GetProperty("webhook_id").GetString());
            Assert.Equal("transmission:one", root.GetProperty("transmission_id").GetString());
            Assert.Equal("event:one", root.GetProperty("webhook_event").GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("webhook_event").ValueKind);
            return Json(request, HttpStatusCode.OK, "{\"verification_status\":\"SUCCESS\"}");
        });
        using var adapter = Adapter(handler);

        var verified = await adapter.VerifyWebhookAsync(
            Evidence(),
            "{\"id\":\"event:one\",\"event_type\":\"PAYMENT.SALE.COMPLETED\"}"u8.ToArray());

        Assert.True(verified);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PayoutSubmissionAndLookup_UseStableIdsAndAsynchronousStateMapping()
    {
        var store = new MemoryReferenceStore();
        var postCalls = 0;
        string? requestId = null;
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index is 0 or 2)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            if (index == 1)
            {
                postCalls++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v1/payments/payouts", request.RequestUri!.AbsolutePath);
                requestId = request.Headers.GetValues("PayPal-Request-Id").Single();
                Assert.StartsWith("dcx-", requestId, StringComparison.Ordinal);
                using var document = JsonDocument.Parse(
                    await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                var root = document.RootElement;
                Assert.Equal(
                    "batch:official-one",
                    root.GetProperty("sender_batch_header").GetProperty("sender_batch_id").GetString());
                Assert.Equal(
                    "EMAIL",
                    root.GetProperty("sender_batch_header").GetProperty("recipient_type").GetString());
                var item = root.GetProperty("items")[0];
                Assert.Equal("3.05", item.GetProperty("amount").GetProperty("value").GetString());
                Assert.Equal("USD", item.GetProperty("amount").GetProperty("currency").GetString());
                Assert.Equal("contributor-one@personal.example.com", item.GetProperty("receiver").GetString());
                Assert.Equal("dcx-000001", item.GetProperty("sender_item_id").GetString());
                Assert.DoesNotContain("contributor:one", await request.Content.ReadAsStringAsync(cancellationToken));
                return Json(request, HttpStatusCode.Created,
                    PayoutResponse("PB-123456", "PENDING", "batch:official-one"));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/payments/payouts/PB-123456", request.RequestUri!.AbsolutePath);
            Assert.Equal(
                "?fields=items&page=1&page_size=100&total_required=true",
                request.RequestUri.Query);
            return Json(request, HttpStatusCode.OK,
                PayoutLookupResponse(
                    "PB-123456", "SUCCESS", "batch:official-one", "SUCCESS", "3.05"));
        });
        using var adapter = Adapter(handler, store: store);
        var preview = Preview("batch:official-one", 305);

        var submitted = await adapter.SendSandboxAsync(preview, CancellationToken.None);
        var lookup = await adapter.LookupSandboxAsync(preview, CancellationToken.None);

        Assert.False(submitted.Succeeded);
        Assert.False(submitted.IsTerminal);
        Assert.Equal("PENDING", submitted.ProviderStatus);
        Assert.Equal("PB-123456", await store.GetProviderBatchIdAsync(preview.BatchId));
        Assert.Equal(SandboxPayoutLookupState.Succeeded, lookup.State);
        Assert.Equal("SUCCESS", lookup.ProviderStatus);
        Assert.Equal(1, postCalls);
        Assert.NotNull(requestId);
        Assert.Equal(68, requestId!.Length);
    }

    [Fact]
    public async Task ExistingProviderReference_UsesLookupInsteadOfResubmitting()
    {
        var store = new MemoryReferenceStore();
        await store.StoreProviderBatchIdAsync("batch:existing", "PB-EXISTING");
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(request, HttpStatusCode.OK,
                PayoutResponse("PB-EXISTING", "PROCESSING", "batch:existing"));
        });
        using var adapter = Adapter(handler, store: store);

        var result = await adapter.SendSandboxAsync(
            Preview("batch:existing", 500), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.IsTerminal);
        Assert.Equal("PROCESSING", result.ProviderStatus);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RetryAfterReferenceStoreFailure_ReusesExactIdempotencyKeys()
    {
        var store = new MemoryReferenceStore { FailNextStore = true };
        var requestIds = new List<string>();
        var bodies = new List<byte[]>();
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index is 0 or 2)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            requestIds.Add(request.Headers.GetValues("PayPal-Request-Id").Single());
            bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return Json(request, HttpStatusCode.Created,
                PayoutResponse(
                    "PB-IDEMPOTENT",
                    index == 1 ? "PENDING" : "PROCESSING",
                    "batch:idempotent-retry"));
        });
        using var adapter = Adapter(handler, store: store);
        var preview = Preview("batch:idempotent-retry", 500);

        var first = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.SendSandboxAsync(preview, CancellationToken.None));
        var retried = await adapter.SendSandboxAsync(preview, CancellationToken.None);

        Assert.Equal("paypal_sandbox_reference_store_failed", first.Code);
        Assert.False(retried.IsTerminal);
        Assert.Equal("PROCESSING", retried.ProviderStatus);
        Assert.Equal(2, requestIds.Count);
        Assert.Equal(requestIds[0], requestIds[1]);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal("PB-IDEMPOTENT", await store.GetProviderBatchIdAsync(preview.BatchId));
    }

    [Fact]
    public async Task WebhookEvidenceAndDuplicateJson_FailBeforeNetwork()
    {
        var handler = new ScriptedHandler((_, request, _) =>
            Task.FromResult(Json(request, HttpStatusCode.InternalServerError, "{}")));
        using var adapter = Adapter(handler);
        var duplicate = "{\"id\":\"event:one\",\"id\":\"event:two\"}"u8.ToArray();

        var duplicateException = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.VerifyWebhookAsync(Evidence(), duplicate));
        Assert.Equal("paypal_sandbox_json_duplicate_property", duplicateException.Code);
        var badEvidence = Evidence() with { CertificateUrl = new Uri("https://attacker.invalid/cert") };
        var evidenceException = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.VerifyWebhookAsync(badEvidence, "{\"id\":\"event:one\"}"u8.ToArray()));
        Assert.Equal("paypal_sandbox_webhook_evidence_invalid", evidenceException.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RedirectedResponseAndProviderNotFound_FailClosedWithoutResubmission()
    {
        var redirect = new ScriptedHandler((_, request, _) =>
        {
            var response = Json(request, HttpStatusCode.OK, TokenResponse());
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Post, "https://api-m.paypal.com/v1/oauth2/token");
            return Task.FromResult(response);
        });
        using (var adapter = Adapter(redirect))
        {
            var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.VerifyWebhookAsync(Evidence(), "{\"id\":\"event:one\"}"u8.ToArray()));
            Assert.Equal("paypal_sandbox_response_origin_invalid", exception.Code);
        }

        var store = new MemoryReferenceStore();
        await store.StoreProviderBatchIdAsync("batch:lost-provider", "PB-LOST");
        var missing = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(request, HttpStatusCode.NotFound, "{\"name\":\"RESOURCE_NOT_FOUND\"}");
        });
        using (var adapter = Adapter(missing, store: store))
        {
            var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.LookupSandboxAsync(
                    Preview("batch:lost-provider", 500), CancellationToken.None));
            Assert.Equal("paypal_sandbox_provider_reference_missing", exception.Code);
            Assert.Equal(2, missing.Calls);
        }
    }

    [Fact]
    public async Task TransportFailure_DoesNotExposeCredentialOrProviderText()
    {
        var handler = new ScriptedHandler((_, _, _) =>
            throw new HttpRequestException(
                $"remote echoed {FixtureCredentialPassword} and {AccessToken}"));
        using var adapter = Adapter(handler);

        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.VerifyWebhookAsync(Evidence(), "{\"id\":\"event:one\"}"u8.ToArray()));

        Assert.Equal("paypal_sandbox_transport_failed", exception.Code);
        Assert.Equal(exception.Code, exception.Message);
        Assert.DoesNotContain(FixtureCredentialPassword, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedBatch_RequiresEveryBoundItemToHaveSucceeded()
    {
        var store = new MemoryReferenceStore();
        await store.StoreProviderBatchIdAsync("batch:partial", "PB-PARTIAL");
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            return Json(request, HttpStatusCode.OK,
                PayoutLookupResponse(
                    "PB-PARTIAL", "SUCCESS", "batch:partial", "FAILED", "5.00"));
        });
        using var adapter = Adapter(handler, store: store);

        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.LookupSandboxAsync(Preview("batch:partial", 500), CancellationToken.None));

        Assert.Equal(
            "paypal_sandbox_partial_payout_requires_item_reconciliation",
            exception.Code);
    }

    [Fact]
    public async Task CompletedBatch_RejectsItemAmountOrBindingMismatch()
    {
        var store = new MemoryReferenceStore();
        await store.StoreProviderBatchIdAsync("batch:mismatch", "PB-MISMATCH");
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            return Json(request, HttpStatusCode.OK,
                PayoutLookupResponse(
                    "PB-MISMATCH", "SUCCESS", "batch:mismatch", "SUCCESS", "5.01"));
        });
        using var adapter = Adapter(handler, store: store);

        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.LookupSandboxAsync(Preview("batch:mismatch", 500), CancellationToken.None));

        Assert.Equal("paypal_sandbox_payout_lookup_response_invalid", exception.Code);
    }

    [Fact]
    public async Task DurableCoordinator_PersistsPendingReferenceAndPostsOnlyAfterItemSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-adapter-{Guid.NewGuid():N}");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            var preview = Preview("batch:durable-adapter", 500);
            var repository = await DurableCareRepository.OpenAsync(root);
            await repository.PostAsync(new LedgerJournalEntry(
                "accrual:durable-adapter",
                now,
                "fixture_contributor_accrual",
                [
                    new LedgerPosting(LedgerAccount.ContributorExpense, 500, 0),
                    new LedgerPosting(LedgerAccount.ContributorPayable, 0, 500),
                ]));
            var submitHandler = new ScriptedHandler(async (index, request, cancellationToken) =>
            {
                if (index == 0)
                {
                    await AssertTokenRequestAsync(request, cancellationToken);
                    return Json(request, HttpStatusCode.OK, TokenResponse());
                }

                return Json(request, HttpStatusCode.Created,
                    PayoutResponse("PB-DURABLE", "PENDING", preview.BatchId));
            });
            using (var submitAdapter = Adapter(submitHandler, store: repository))
            {
                var submitted = await new DurablePayoutCoordinator(repository, submitAdapter).ExecuteAsync(
                    preview, true, true, preview.EligibilityDate, now);
                Assert.False(submitted.IsTerminal);
            }

            Assert.Equal("PB-DURABLE", await repository.GetProviderBatchIdAsync(preview.BatchId));
            Assert.Equal("reserved", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);

            var reopened = await DurableCareRepository.OpenAsync(root);
            var lookupHandler = new ScriptedHandler(async (index, request, cancellationToken) =>
            {
                if (index == 0)
                {
                    await AssertTokenRequestAsync(request, cancellationToken);
                    return Json(request, HttpStatusCode.OK, TokenResponse());
                }

                return Json(request, HttpStatusCode.OK,
                    PayoutLookupResponse(
                        "PB-DURABLE", "SUCCESS", preview.BatchId, "SUCCESS", "5.00"));
            });
            using var lookupAdapter = Adapter(lookupHandler, store: reopened);
            var summary = await new DurablePayoutCoordinator(reopened, lookupAdapter)
                .ReconcilePendingAsync(now.AddMinutes(1), now.AddMinutes(1));

            Assert.Equal(new PayoutReconciliationSummary(1, 1, 0, 0), summary);
            Assert.Equal("succeeded", (await reopened.GetPayoutStateAsync(preview.BatchId))!.Status);
            Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
            Assert.Equal(-500, await reopened.BalanceAsync(LedgerAccount.Cash));
            Assert.True(await reopened.ReconcilesAsync());
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task OversizedAndDuplicateResponsesFailClosed()
    {
        var oversizedHandler = new ScriptedHandler((_, request, _) => Task.FromResult(
            Json(request, HttpStatusCode.OK,
                new string('x', PayPalSandboxHttpAdapter.MaximumResponseBytes + 1))));
        using (var adapter = Adapter(oversizedHandler))
        {
            var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.VerifyWebhookAsync(Evidence(), "{\"id\":\"event:one\"}"u8.ToArray()));
            Assert.Equal("paypal_sandbox_response_oversized", exception.Code);
        }

        var duplicateHandler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK,
                    "{\"access_token\":\"one\",\"access_token\":\"two\",\"token_type\":\"Bearer\"}");
            }

            throw new InvalidOperationException("The duplicate token fixture unexpectedly continued.");
        });
        using (var adapter = Adapter(duplicateHandler))
        {
            var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.VerifyWebhookAsync(Evidence(), "{\"id\":\"event:one\"}"u8.ToArray()));
            Assert.Equal("paypal_sandbox_json_duplicate_property", exception.Code);
            Assert.Equal(1, duplicateHandler.Calls);
        }
    }

    [Fact]
    public async Task IneligibleOrDuplicateRecipient_IsDeniedBeforeCredentialUse()
    {
        var handler = new ScriptedHandler((_, request, _) =>
            Task.FromResult(Json(request, HttpStatusCode.OK, TokenResponse())));
        using var ineligible = new PayPalSandboxHttpAdapter(
            handler,
            new StaticCredentialSource(),
            "WHDCXSANDBOX",
            new StaticRecipientDirectory(new PayPalSandboxRecipient(
                "not-eligible@personal.example.com", false)),
            new MemoryReferenceStore());
        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            ineligible.SendSandboxAsync(Preview("batch:ineligible", 500), CancellationToken.None));
        Assert.Equal("paypal_sandbox_recipient_ineligible", exception.Code);
        Assert.Equal(0, handler.Calls);

        var duplicateDirectory = new StaticRecipientDirectory(
            new PayPalSandboxRecipient("same@personal.example.com", true));
        using var duplicate = new PayPalSandboxHttpAdapter(
            new ScriptedHandler((_, request, _) =>
                Task.FromResult(Json(request, HttpStatusCode.OK, TokenResponse()))),
            new StaticCredentialSource(),
            "WHDCXSANDBOX",
            duplicateDirectory,
            new MemoryReferenceStore());
        var duplicatePreview = new PayoutBatchPreview(
            "batch:duplicate-recipient",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [new PayoutItem("contributor:one", 100), new PayoutItem("contributor:two", 200)]);
        var duplicateException = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            duplicate.SendSandboxAsync(duplicatePreview, CancellationToken.None));
        Assert.Equal("paypal_sandbox_recipient_ineligible", duplicateException.Code);
    }

    [Fact]
    public void ProductionTransport_IsPinnedToSandboxAndDisablesAmbientRouting()
    {
        Assert.Equal("https://api-m.sandbox.paypal.com/", PayPalSandboxHttpAdapter.SandboxOrigin.AbsoluteUri);
        using var handler = PayPalSandboxHttpAdapter.CreateHardenedHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Fact]
    public async Task CallerCancellation_IsPreserved()
    {
        var handler = new CancellationHandler();
        using var adapter = Adapter(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = adapter.VerifyWebhookAsync(
            Evidence(), "{\"id\":\"event:one\"}"u8.ToArray(), cancellation.Token);
        await handler.Started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static PayPalSandboxHttpAdapter Adapter(
        HttpMessageHandler handler,
        IPayPalSandboxRecipientDirectory? directory = null,
        IPayPalSandboxPayoutReferenceStore? store = null) =>
        new(
            handler,
            new StaticCredentialSource(),
            "WHDCXSANDBOX",
            directory ?? new StaticRecipientDirectory(new PayPalSandboxRecipient(
                "contributor-one@personal.example.com", true)),
            store ?? new MemoryReferenceStore());

    private static PayoutBatchPreview Preview(string batchId, long cents) => new(
        batchId,
        PayoutExecutionMode.Sandbox,
        new DateOnly(2026, 4, 1),
        [new PayoutItem("contributor:one", cents)]);

    private static PayPalSandboxWebhookVerificationEvidence Evidence() => new(
        "SHA256withRSA",
        new Uri("https://api.sandbox.paypal.com/v1/notifications/certs/CERT-DCX123"),
        "transmission:one",
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 64).ToArray()),
        "2026-10-17T01:02:03Z");

    private static string TokenResponse() =>
        $"{{\"access_token\":\"{AccessToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}";

    private static string PayoutResponse(string providerId, string status, string senderId) =>
        $"{{\"batch_header\":{{\"payout_batch_id\":\"{providerId}\",\"batch_status\":\"{status}\",\"sender_batch_header\":{{\"sender_batch_id\":\"{senderId}\"}}}}}}";

    private static string PayoutLookupResponse(
        string providerId,
        string batchStatus,
        string senderId,
        string transactionStatus,
        string amount) =>
        $"{{\"total_items\":1,\"total_pages\":1,\"batch_header\":{{\"payout_batch_id\":\"{providerId}\",\"batch_status\":\"{batchStatus}\",\"sender_batch_header\":{{\"sender_batch_id\":\"{senderId}\"}}}},\"items\":[{{\"payout_item_id\":\"PI-000001\",\"payout_batch_id\":\"{providerId}\",\"transaction_status\":\"{transactionStatus}\",\"payout_item\":{{\"amount\":{{\"currency\":\"USD\",\"value\":\"{amount}\"}},\"receiver\":\"contributor-one@personal.example.com\",\"sender_item_id\":\"dcx-000001\"}}}}]}}";

    private static async Task AssertTokenRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api-m.sandbox.paypal.com/v1/oauth2/token", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{ClientId}:{FixtureCredentialPassword}")),
            request.Headers.Authorization.Parameter);
        Assert.Equal(
            "application/x-www-form-urlencoded",
            request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("grant_type=client_credentials", await request.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpResponseMessage Json(
        HttpRequestMessage request,
        HttpStatusCode status,
        string body) => new(status)
    {
        RequestMessage = request,
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StaticCredentialSource : IPayPalSandboxCredentialSource
    {
        public ValueTask<PayPalSandboxCredentialLease> AcquireAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(PayPalSandboxCredentialLease.Create(
                Encoding.ASCII.GetBytes(ClientId),
                Encoding.ASCII.GetBytes(FixtureCredentialPassword)));
    }

    private sealed class StaticRecipientDirectory(PayPalSandboxRecipient recipient)
        : IPayPalSandboxRecipientDirectory
    {
        public ValueTask<PayPalSandboxRecipient?> ResolveAsync(
            string contributorPseudonym,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PayPalSandboxRecipient?>(recipient);
    }

    private sealed class MemoryReferenceStore : IPayPalSandboxPayoutReferenceStore
    {
        private readonly Dictionary<string, string> references = new(StringComparer.Ordinal);

        public bool FailNextStore { get; set; }

        public Task<string?> GetProviderBatchIdAsync(
            string localBatchId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(references.GetValueOrDefault(localBatchId));

        public Task StoreProviderBatchIdAsync(
            string localBatchId,
            string providerBatchId,
            CancellationToken cancellationToken = default)
        {
            if (FailNextStore)
            {
                FailNextStore = false;
                throw new IOException("simulated durable store interruption");
            }

            if (references.TryGetValue(localBatchId, out var existing) && existing != providerBatchId)
            {
                throw new InvalidOperationException("conflict");
            }

            references[localBatchId] = providerBatchId;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int calls;

        public int Calls => calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(Interlocked.Increment(ref calls) - 1, request, cancellationToken);
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation fixture continued unexpectedly.");
        }
    }
}
