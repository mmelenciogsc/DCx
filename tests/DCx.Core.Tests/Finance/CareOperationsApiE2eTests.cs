using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.Finance;
using DCx.GlobalService;

namespace DCx.Core.Tests.Finance;

public sealed class CareOperationsApiE2eTests : IDisposable
{
    private const string PlanId = "P-DCX300";
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-care-api-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoopbackApi_AuthenticatesBindsExactReviewAndNeverCallsProviderDuringReplay()
    {
        Directory.CreateDirectory(root);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "simulation-key.pk8");
        await File.WriteAllBytesAsync(keyPath, signingKey.ExportPkcs8PrivateKey());
        var operatorToken = $"care-operator-{Guid.NewGuid():N}";
        var wrongToken = $"wrong-operator-{Guid.NewGuid():N}";
        var contributorToken = $"care-contributor-{Guid.NewGuid():N}";
        const string contributorPseudonym = "contributor:care-api-fixture";
        var operatorPseudonymSha256 = Digest("care-operator-fixture");
        var dataRoot = Path.Combine(root, "server");
        var careRoot = Path.Combine(dataRoot, "care");
        var repository = await DurableCareRepository.OpenAsync(careRoot);
        var providerHandler = VerifiedWebhookHandler();
        using var adapter = Adapter(providerHandler);
        var received = DateTimeOffset.UtcNow.AddMinutes(-1).ToUniversalTime();
        await CreateBlockedRefundAsync(repository, adapter, received);
        await QueuePaymentAsync(
            repository,
            adapter,
            "FIRST",
            "I-CARE-API-FIRST",
            received);
        await ActivateAsync(
            repository,
            adapter,
            "FIRST",
            "I-CARE-API-FIRST",
            received);
        Assert.Equal(12, providerHandler.Calls);

        var endpoint = new Uri($"http://127.0.0.1:{ReserveEphemeralPort()}/");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(GlobalRegistryRepository).Assembly.Location);
        startInfo.Environment["DCX_GLOBAL_SIMULATION"] = "1";
        startInfo.Environment["DCX_GLOBAL_DATA_ROOT"] = dataRoot;
        startInfo.Environment["DCX_GLOBAL_SIGNING_KEY"] = keyPath;
        startInfo.Environment["DCX_GLOBAL_SIMULATION_URL"] = endpoint.AbsoluteUri;
        startInfo.Environment["DCX_GLOBAL_CARE_OPERATIONS_API"] = "1";
        startInfo.Environment["DCX_GLOBAL_CARE_PROCESSING"] = "1";
        startInfo.Environment["DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256"] = Digest(operatorToken);
        startInfo.Environment["DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256"] =
            operatorPseudonymSha256;
        startInfo.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_DASHBOARD_API"] = "1";
        startInfo.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_TOKEN_SHA256"] =
            Digest(contributorToken);
        startInfo.Environment["DCX_GLOBAL_CARE_CONTRIBUTOR_PSEUDONYM"] =
            contributorPseudonym;
        startInfo.Environment["DCX_GLOBAL_CARE_MONITOR"] = "1";
        startInfo.Environment["DCX_GLOBAL_CARE_MONITOR_INTERVAL_MS"] = "100";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the CARE operations fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await WaitForHealthAsync(client, endpoint, process, errorTask);
            using (var healthDocument = JsonDocument.Parse(
                       await client.GetStringAsync(new Uri(endpoint, "health"))))
            {
                Assert.True(healthDocument.RootElement.GetProperty("careOperationsApi").GetBoolean());
                Assert.True(healthDocument.RootElement.GetProperty("careOperationsProcessing").GetBoolean());
                Assert.True(healthDocument.RootElement.GetProperty("careOperationsRateLimit").GetBoolean());
                Assert.True(healthDocument.RootElement.GetProperty("careOperationsMonitor").GetBoolean());
                Assert.True(healthDocument.RootElement.GetProperty("careContributorDashboardApi").GetBoolean());
                Assert.True(healthDocument.RootElement.GetProperty("careContributorDashboardRateLimit").GetBoolean());
            }
            await Task.Delay(250);

            var healthUri = new Uri(endpoint, "api/care/operations/health");
            using (var missingAuth = await client.GetAsync(healthUri))
            {
                await AssertErrorAsync(
                    missingAuth, HttpStatusCode.Unauthorized, "authentication_required");
            }
            using (var invalidAuth = await SendAuthorizedAsync(
                       client, HttpMethod.Get, healthUri, wrongToken))
            {
                await AssertErrorAsync(
                    invalidAuth, HttpStatusCode.Unauthorized, "authentication_invalid");
            }
            using (var contributorCannotOperate = await SendAuthorizedAsync(
                       client, HttpMethod.Get, healthUri, contributorToken))
            {
                await AssertErrorAsync(
                    contributorCannotOperate,
                    HttpStatusCode.Unauthorized,
                    "authentication_invalid");
            }

            var contributorUri = new Uri(endpoint, "api/care/contributor-dashboard");
            using (var missingContributorAuth = await client.GetAsync(contributorUri))
            {
                await AssertErrorAsync(
                    missingContributorAuth,
                    HttpStatusCode.Unauthorized,
                    "authentication_required");
                Assert.True(missingContributorAuth.Headers.CacheControl?.NoStore);
            }
            using (var operatorCannotReadContributor = await SendAuthorizedAsync(
                       client, HttpMethod.Get, contributorUri, operatorToken))
            {
                await AssertErrorAsync(
                    operatorCannotReadContributor,
                    HttpStatusCode.Unauthorized,
                    "authentication_invalid");
            }
            using (var contributorSource = new LoopbackCareContributorDashboardSource(
                       endpoint,
                       contributorToken,
                       contributorPseudonym,
                       TimeSpan.FromSeconds(5)))
            {
                var contributorSnapshot = await contributorSource.LoadAsync();
                Assert.Equal(
                    CareContributorDashboardPolicy.Available,
                    contributorSnapshot.StatusCode);
                Assert.Equal(contributorPseudonym, contributorSnapshot.ContributorPseudonym);
                Assert.Empty(contributorSnapshot.Periods);
            }
            using (var substitutedSubjectSource = new LoopbackCareContributorDashboardSource(
                       endpoint,
                       contributorToken,
                       "contributor:wrong-client-subject",
                       TimeSpan.FromSeconds(5)))
            {
                var subjectError = await Assert.ThrowsAsync<
                    CareContributorDashboardTransportException>(
                    () => substitutedSubjectSource.LoadAsync());
                Assert.Equal("care_contributor_response_invalid", subjectError.Code);
            }
            using (var malformed = await SendRawProcessAsync(
                       client,
                       new Uri(endpoint, "api/care/operations/process"),
                       operatorToken,
                       "{}"u8.ToArray()))
            {
                await AssertErrorAsync(
                    malformed, HttpStatusCode.BadRequest, "care_operations_request_invalid");
            }
            Assert.Empty(await repository.GetCareOperatorActionAuditAsync());

            using var source = new LoopbackCareOperationsSource(
                endpoint, operatorToken, TimeSpan.FromSeconds(5));
            var firstReview = await source.RefreshAsync();
            Assert.Equal(1, firstReview.Due);
            Assert.True(firstReview.CanProcessDue);
            var unapprovedRequestJson = JsonSerializer.Serialize(
                new CareOperationsProcessRequest(firstReview, ExplicitlyApproved: false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var duplicateRequestJson = unapprovedRequestJson.Replace(
                "\"explicitlyApproved\":false",
                "\"explicitlyApproved\":false,\"explicitlyApproved\":false",
                StringComparison.Ordinal);
            using (var duplicateRequest = await SendRawProcessAsync(
                       client,
                       new Uri(endpoint, "api/care/operations/process"),
                       operatorToken,
                       Encoding.UTF8.GetBytes(duplicateRequestJson)))
            {
                await AssertErrorAsync(
                    duplicateRequest,
                    HttpStatusCode.BadRequest,
                    "care_operations_request_invalid");
            }
            using (var encodedRequest = await SendRawProcessAsync(
                       client,
                       new Uri(endpoint, "api/care/operations/process"),
                       operatorToken,
                       Encoding.UTF8.GetBytes(unapprovedRequestJson),
                       contentEncoding: "gzip"))
            {
                await AssertErrorAsync(
                    encodedRequest,
                    HttpStatusCode.BadRequest,
                    "care_operations_request_invalid");
            }
            using (var wrongCharsetRequest = await SendRawProcessAsync(
                       client,
                       new Uri(endpoint, "api/care/operations/process"),
                       operatorToken,
                       Encoding.UTF8.GetBytes(unapprovedRequestJson),
                       charset: "utf-16"))
            {
                await AssertErrorAsync(
                    wrongCharsetRequest,
                    HttpStatusCode.BadRequest,
                    "care_operations_request_invalid");
            }
            var serializedHealth = JsonSerializer.Serialize(firstReview);
            Assert.DoesNotContain("FIRST", serializedHealth, StringComparison.Ordinal);
            Assert.DoesNotContain("subscription", serializedHealth, StringComparison.OrdinalIgnoreCase);

            var blockedCases = await source.ListBlockedCasesAsync();
            var blockedCase = Assert.Single(blockedCases);
            var serializedCase = JsonSerializer.Serialize(blockedCase);
            Assert.DoesNotContain("BLOCKED", serializedCase, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("subscription", serializedCase, StringComparison.OrdinalIgnoreCase);
            var unapprovedDisposition = await Assert.ThrowsAsync<CareOperationsTransportException>(
                () => source.RecordDispositionAsync(new CareBlockedCaseDispositionRequest(
                    blockedCase.CaseId,
                    blockedCase.ReviewSha256,
                    CareBlockedCaseReviewPolicy.InvestigationRequested,
                    ExplicitlyApproved: false)));
            Assert.Equal("care_blocked_case_approval_required", unapprovedDisposition.Code);
            var staleDisposition = await Assert.ThrowsAsync<CareOperationsTransportException>(
                () => source.RecordDispositionAsync(new CareBlockedCaseDispositionRequest(
                    blockedCase.CaseId,
                    new string('f', 64),
                    CareBlockedCaseReviewPolicy.InvestigationRequested,
                    ExplicitlyApproved: true)));
            Assert.Equal("care_blocked_case_review_stale", staleDisposition.Code);
            var disposition = await source.RecordDispositionAsync(
                new CareBlockedCaseDispositionRequest(
                    blockedCase.CaseId,
                    blockedCase.ReviewSha256,
                    CareBlockedCaseReviewPolicy.InvestigationRequested,
                    ExplicitlyApproved: true));
            Assert.Equal(0, disposition.ProviderCalls);
            Assert.Equal(0, disposition.FinancialMutations);
            Assert.Equal(
                CareBlockedCaseReviewPolicy.InvestigationRequested,
                Assert.Single(await source.ListBlockedCasesAsync()).DispositionCode);
            Assert.Single(await repository.GetBlockedCaseDispositionAuditAsync());
            Assert.Equal(12, providerHandler.Calls);

            var processUri = new Uri(endpoint, "api/care/operations/process");
            using (var unapproved = await SendProcessAsync(
                       client, processUri, operatorToken, firstReview, explicitlyApproved: false))
            {
                await AssertErrorAsync(
                    unapproved, HttpStatusCode.Forbidden, "care_operations_approval_required");
            }
            Assert.Equal(0, (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-CARE-API-FIRST"))!.AttemptCount);

            await QueuePaymentAsync(
                repository,
                adapter,
                "SECOND",
                "I-CARE-API-SECOND",
                received);
            var stale = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
                source.ProcessDueAsync(firstReview, explicitlyApproved: true));
            Assert.Equal("care_operations_review_stale", stale.Code);
            await ActivateAsync(
                repository,
                adapter,
                "SECOND",
                "I-CARE-API-SECOND",
                received);
            Assert.Equal(16, providerHandler.Calls);

            var exactReview = await source.RefreshAsync();
            Assert.Equal(2, exactReview.Due);
            var result = await source.ProcessDueAsync(exactReview, explicitlyApproved: true);

            Assert.Equal(2, result.Scanned);
            Assert.Equal(2, result.Completed);
            Assert.Equal(0, result.ProviderCalls);
            Assert.Equal("operator_action_required", result.Health.StatusCode);
            Assert.Equal(800, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(16, providerHandler.Calls);
            var replay = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
                source.ProcessDueAsync(exactReview, explicitlyApproved: true));
            Assert.Equal("care_operations_review_stale", replay.Code);
            Assert.Equal(800, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(16, providerHandler.Calls);
            var audit = await repository.GetCareOperatorActionAuditAsync();
            Assert.Equal(
                [
                    CareOperatorActionAuditIntegrity.ApprovalRequiredDecision,
                    CareOperatorActionAuditIntegrity.ReviewStaleDecision,
                    CareOperatorActionAuditIntegrity.ProcessedDecision,
                    CareOperatorActionAuditIntegrity.ReviewStaleDecision,
                ],
                audit.Select(record => record.DecisionCode).ToArray());
            Assert.Equal([1L, 2L, 3L, 4L], audit.Select(record => record.Sequence).ToArray());
            Assert.All(audit, record =>
            {
                Assert.Equal(operatorPseudonymSha256, record.OperatorPseudonymSha256);
                Assert.Equal(CareOperatorActionAuditIntegrity.ProcessDueAction, record.ActionCode);
                Assert.Equal(64, record.ReviewedHealthSha256.Length);
                Assert.Equal(64, record.RecordSha256.Length);
            });
            Assert.Null(audit[0].ResultSha256);
            Assert.NotNull(audit[2].ResultSha256);
            var serializedAudit = JsonSerializer.Serialize(audit);
            Assert.DoesNotContain(operatorToken, serializedAudit, StringComparison.Ordinal);
            Assert.DoesNotContain("WH-CARE-API", serializedAudit, StringComparison.Ordinal);
            Assert.DoesNotContain("I-CARE-API", serializedAudit, StringComparison.Ordinal);

            var failedPayout = await CreateFailedPayoutAsync(repository, received);
            var cashBeforeDecision = await repository.BalanceAsync(LedgerAccount.Cash);
            var payableBeforeDecision = await repository.BalanceAsync(
                LedgerAccount.ContributorPayable);
            var holdsUri = new Uri(endpoint, "api/care/operations/failed-payout-holds");
            using (var contributorCannotReadHolds = await SendAuthorizedAsync(
                       client, HttpMethod.Get, holdsUri, contributorToken))
            {
                await AssertErrorAsync(
                    contributorCannotReadHolds,
                    HttpStatusCode.Unauthorized,
                    "authentication_invalid");
                Assert.True(contributorCannotReadHolds.Headers.CacheControl?.NoStore);
            }

            var hold = Assert.Single(await source.ListFailedPayoutReviewsAsync());
            Assert.Equal(failedPayout.CarePeriodId, hold.CarePeriodId);
            Assert.Equal(700, hold.AmountCents);
            Assert.Equal(CareFailedPayoutReviewPolicy.ProviderRejected, hold.FailureCode);
            var serializedHold = JsonSerializer.Serialize(hold);
            Assert.DoesNotContain("batch:care-api-hold", serializedHold, StringComparison.Ordinal);
            Assert.DoesNotContain("contributor:", serializedHold, StringComparison.Ordinal);
            Assert.DoesNotContain("personal.example", serializedHold, StringComparison.Ordinal);
            Assert.DoesNotContain("snapshot:", serializedHold, StringComparison.Ordinal);

            var approvedHoldRequest = new CareFailedPayoutDispositionRequest(
                hold.ReviewId,
                hold.ReviewSha256,
                CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
                ExplicitlyApproved: true);
            var holdDispositionUri = new Uri(
                endpoint,
                $"api/care/operations/failed-payout-holds/{hold.ReviewId}/disposition");
            var unapprovedHoldBody = JsonSerializer.SerializeToUtf8Bytes(
                approvedHoldRequest with { ExplicitlyApproved = false },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var unapprovedHold = await SendRawProcessAsync(
                       client,
                       holdDispositionUri,
                       operatorToken,
                       unapprovedHoldBody))
            {
                await AssertErrorAsync(
                    unapprovedHold,
                    HttpStatusCode.Forbidden,
                    "care_failed_payout_approval_required");
                Assert.True(unapprovedHold.Headers.CacheControl?.NoStore);
            }

            var mismatchedRouteBody = JsonSerializer.SerializeToUtf8Bytes(
                approvedHoldRequest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var mismatchedRoute = await SendRawProcessAsync(
                       client,
                       new Uri(
                           endpoint,
                           "api/care/operations/failed-payout-holds/ffffffffffffffffffffffffffffffff/disposition"),
                       operatorToken,
                       mismatchedRouteBody))
            {
                await AssertErrorAsync(
                    mismatchedRoute,
                    HttpStatusCode.BadRequest,
                    "care_failed_payout_request_invalid");
            }

            var staleHold = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
                source.RecordFailedPayoutDispositionAsync(approvedHoldRequest with
                {
                    ReviewedSha256 = new string('f', 64),
                }));
            Assert.Equal("care_failed_payout_review_stale", staleHold.Code);
            var holdResult = await source.RecordFailedPayoutDispositionAsync(
                approvedHoldRequest);
            CareFailedPayoutReviewPolicy.RequireResult(holdResult, approvedHoldRequest);
            Assert.Equal(0, holdResult.ProviderCalls);
            Assert.Equal(0, holdResult.FinancialMutations);
            Assert.Equal(0, holdResult.PayoutAttempts);
            Assert.Equal(0, holdResult.NotificationTransports);
            Assert.Equal(
                holdResult,
                await source.RecordFailedPayoutDispositionAsync(approvedHoldRequest));

            var updatedHold = Assert.Single(await source.ListFailedPayoutReviewsAsync());
            Assert.Equal(
                CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
                updatedHold.DispositionCode);
            Assert.Equal(
                CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
                updatedHold.NoticeCode);
            Assert.NotEqual(hold.ReviewSha256, updatedHold.ReviewSha256);
            var conflictingHold = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
                source.RecordFailedPayoutDispositionAsync(new CareFailedPayoutDispositionRequest(
                    updatedHold.ReviewId,
                    updatedHold.ReviewSha256,
                    CareFailedPayoutReviewPolicy.CancelRetryIntent,
                    ExplicitlyApproved: true)));
            Assert.Equal("care_failed_payout_conflict", conflictingHold.Code);
            var holdAudit = Assert.Single(
                await repository.GetFailedPayoutDispositionAuditAsync());
            Assert.Equal(operatorPseudonymSha256, holdAudit.OperatorPseudonymSha256);
            Assert.Equal(hold.ReviewSha256, holdAudit.ReviewedSha256);
            Assert.Equal(cashBeforeDecision, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(
                payableBeforeDecision,
                await repository.BalanceAsync(LedgerAccount.ContributorPayable));
            Assert.Equal(16, providerHandler.Calls);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            var diagnostics = await outputTask + await errorTask;
            Assert.Contains(
                CareOperationsMonitor.OperatorActionRequiredCode,
                diagnostics,
                StringComparison.Ordinal);
            Assert.DoesNotContain(operatorToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(contributorToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("WH-CARE-API", diagnostics, StringComparison.Ordinal);
        }
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static async Task QueuePaymentAsync(
        DurableCareRepository repository,
        PayPalSandboxHttpAdapter adapter,
        string suffix,
        string subscriptionId,
        DateTimeOffset received)
    {
        var webhook = OfficialWebhook(
            $"WH-CARE-API-{suffix}",
            "PAYMENT.SALE.COMPLETED",
            "sale",
            $"{{\"id\":\"SALE-CARE-API-{suffix}\",\"state\":\"completed\",\"billing_agreement_id\":\"{subscriptionId}\",\"amount\":{{\"total\":\"3.00\",\"currency\":\"USD\"}}}}");
        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter,
            Evidence($"transmission:care-api-payment-{suffix.ToLowerInvariant()}"),
            webhook,
            PlanId,
            received));
    }

    private static async Task CreateBlockedRefundAsync(
        DurableCareRepository repository,
        PayPalSandboxHttpAdapter adapter,
        DateTimeOffset received)
    {
        var delayedRefund = OfficialWebhook(
            "WH-CARE-API-BLOCKED-REFUND",
            "PAYMENT.SALE.REFUNDED",
            "refund",
            "{\"id\":\"REFUND-CARE-API-BLOCKED\",\"state\":\"completed\",\"sale_id\":\"SALE-CARE-API-BLOCKED\",\"amount\":{\"total\":\"2.50\",\"currency\":\"USD\"}}");
        var activation = OfficialWebhook(
            "WH-CARE-API-BLOCKED-ACTIVE",
            "BILLING.SUBSCRIPTION.ACTIVATED",
            "subscription",
            $"{{\"id\":\"I-CARE-API-BLOCKED\",\"plan_id\":\"{PlanId}\",\"status\":\"ACTIVE\"}}");
        var payment = OfficialWebhook(
            "WH-CARE-API-BLOCKED-PAYMENT",
            "PAYMENT.SALE.COMPLETED",
            "sale",
            "{\"id\":\"SALE-CARE-API-BLOCKED\",\"state\":\"completed\",\"billing_agreement_id\":\"I-CARE-API-BLOCKED\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
        var acceptedRefund = OfficialWebhook(
            "WH-CARE-API-BLOCKED-ACCEPTED",
            "PAYMENT.SALE.REFUNDED",
            "refund",
            "{\"id\":\"REFUND-CARE-API-ACCEPTED\",\"state\":\"completed\",\"sale_id\":\"SALE-CARE-API-BLOCKED\",\"amount\":{\"total\":\"1.00\",\"currency\":\"USD\"}}");

        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter, Evidence("transmission:care-api-blocked-refund"),
            delayedRefund, PlanId, received));
        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter, Evidence("transmission:care-api-blocked-active"),
            activation, PlanId, received));
        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter, Evidence("transmission:care-api-blocked-payment"),
            payment, PlanId, received));
        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter, Evidence("transmission:care-api-blocked-accepted"),
            acceptedRefund, PlanId, received));
        Assert.Equal(
            new WebhookQuarantineReprocessSummary(1, 0, 0, 1),
            await repository.ReprocessQuarantinedOfficialWebhooksAsync(received));
    }

    private static async Task ActivateAsync(
        DurableCareRepository repository,
        PayPalSandboxHttpAdapter adapter,
        string suffix,
        string subscriptionId,
        DateTimeOffset received)
    {
        var webhook = OfficialWebhook(
            $"WH-CARE-API-{suffix}-ACTIVE",
            "BILLING.SUBSCRIPTION.ACTIVATED",
            "subscription",
            $"{{\"id\":\"{subscriptionId}\",\"plan_id\":\"{PlanId}\",\"status\":\"ACTIVE\"}}");
        Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
            adapter,
            Evidence($"transmission:care-api-active-{suffix.ToLowerInvariant()}"),
            webhook,
            PlanId,
            received));
    }

    private static async Task<PayoutBatchPreview> CreateFailedPayoutAsync(
        DurableCareRepository repository,
        DateTimeOffset now)
    {
        var usage = new[]
        {
            new ContributorUsageWeight("contributor:hold-one", 500),
            new ContributorUsageWeight("contributor:hold-two", 700),
        };
        var closeReview = DurableCareRepository.ReviewCarePeriodClose(
            new CarePeriodInput(
                2026,
                5,
                SubscriptionRevenueCents: 3000,
                RefundAndChargebackCents: 0,
                PaymentFeeCents: 0,
                ApprovedOperatingCostCents: 0,
                TaxAndRiskReserveCents: 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 0),
            usage,
            now);
        var period = await repository.RegisterReviewedCarePeriodAsync(
            closeReview,
            ownerApproved: true,
            now);
        var preview = new PayoutBatchPreview(
            "batch:care-api-hold",
            PayoutExecutionMode.Sandbox,
            period.Net60EligibilityDate,
            [
                new PayoutItem(
                    usage[0].ContributorPseudonym,
                    decimal.ToInt64(usage[0].Weight),
                    PayoutRecipientApproval.Create(
                        "snapshot:care-api-hold-one",
                        "care-api-hold-one@personal.example.com")),
                new PayoutItem(
                    usage[1].ContributorPseudonym,
                    decimal.ToInt64(usage[1].Weight),
                    PayoutRecipientApproval.Create(
                        "snapshot:care-api-hold-two",
                        "care-api-hold-two@personal.example.com")),
            ],
            PayoutBatchPreviewFactory.CarePeriodId(period));
        Assert.True(await repository.ReservePayoutAsync(
            preview,
            accountEligibilityVerified: true,
            ownerApproved: true,
            preview.EligibilityDate,
            now));
        Assert.True(await repository.CompletePayoutAsync(
            preview,
            new PayoutBatchResult(
                preview.BatchId,
                PayoutBatchOutcome.PartiallySucceeded,
                "SUCCESS",
                [
                    new PayoutItemResult(
                        usage[0].ContributorPseudonym,
                        decimal.ToInt64(usage[0].Weight),
                        Succeeded: true,
                        ProviderStatus: "SUCCESS"),
                    new PayoutItemResult(
                        usage[1].ContributorPseudonym,
                        decimal.ToInt64(usage[1].Weight),
                        Succeeded: false,
                        ProviderStatus: "FAILED"),
                ]),
            now));
        return preview;
    }

    private static PayPalSandboxHttpAdapter Adapter(HttpMessageHandler handler) => new(
        handler,
        new StaticCredentialSource(),
        "WHDCXSANDBOX",
        new EmptyRecipientDirectory(),
        new MemoryReferenceStore());

    private static ScriptedHandler VerifiedWebhookHandler() => new(
        (index, request, _) => Task.FromResult(index % 2 == 0
            ? Json(request, HttpStatusCode.OK,
                "{\"access_token\":\"sandbox-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}")
            : Json(request, HttpStatusCode.OK, "{\"verification_status\":\"SUCCESS\"}")));

    private static PayPalSandboxWebhookVerificationEvidence Evidence(string transmissionId) => new(
        "SHA256withRSA",
        new Uri("https://api.sandbox.paypal.com/v1/notifications/certs/CERT-DCX123"),
        transmissionId,
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 64).ToArray()),
        DateTimeOffset.UtcNow.ToString("O"));

    private static byte[] OfficialWebhook(
        string eventId,
        string eventType,
        string resourceType,
        string resourceJson) => Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{eventId}\",\"create_time\":\"{DateTimeOffset.UtcNow:O}\",\"resource_type\":\"{resourceType}\",\"event_type\":\"{eventType}\",\"resource\":{resourceJson}}}");

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        string token)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendProcessAsync(
        HttpClient client,
        Uri uri,
        string token,
        CareOperationsHealth health,
        bool explicitlyApproved)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new CareOperationsProcessRequest(health, explicitlyApproved),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await client.SendAsync(request);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<HttpResponseMessage> SendRawProcessAsync(
        HttpClient client,
        Uri uri,
        string token,
        byte[] body,
        string? charset = null,
        string? contentEncoding = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentType.CharSet = charset;
        if (contentEncoding is not null)
        {
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await client.SendAsync(request);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHealthAsync(
        HttpClient client,
        Uri endpoint,
        Process process,
        Task<string> errorTask)
    {
        var health = new Uri(endpoint, "health");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The CARE operations API fixture exited early: {await errorTask}");
            }

            try
            {
                using var response = await client.GetAsync(health);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The CARE operations API fixture did not become healthy.");
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
                "client-id"u8, "client-secret"u8));
    }

    private sealed class EmptyRecipientDirectory : IPayPalSandboxRecipientDirectory
    {
        public ValueTask<PayPalSandboxRecipient?> ResolveAsync(
            string snapshotId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PayPalSandboxRecipient?>(null);
    }

    private sealed class MemoryReferenceStore : IPayPalSandboxPayoutReferenceStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public Task<string?> GetProviderBatchIdAsync(
            string localBatchId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(localBatchId));

        public Task StoreProviderBatchIdAsync(
            string localBatchId,
            string providerBatchId,
            CancellationToken cancellationToken = default)
        {
            values[localBatchId] = providerBatchId;
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
}
