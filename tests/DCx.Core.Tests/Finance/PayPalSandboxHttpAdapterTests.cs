using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DCx.Core.Finance;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Tests.Finance;

public sealed class PayPalSandboxHttpAdapterTests
{
    private const string DefaultContributor = "contributor:one";
    private const string DefaultSnapshotId = "snapshot:contributor-one";
    private const string DefaultEmail = "contributor-one@personal.example.com";
    private const string ClientId = "sandbox-client-id";
    private const string FixtureCredentialPassword = "sandbox-client-secret-never-log";
    private const string AccessToken = "sandbox-access-token-never-log";

    [Fact]
    public void QuarantineHealthContract_IsAggregateAndIdentifierFree()
    {
        Assert.Equal(
            [
                "StatusCode",
                "Pending",
                "Due",
                "Blocked",
                "Completed",
                "MaximumAttemptCount",
                "OldestPendingUtc",
                "NextAttemptUtc",
            ],
            typeof(WebhookQuarantineHealthSnapshot)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task DurableOperationsSource_DefaultCapabilityDeniesDueProcessing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-care-operations-disabled-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            var received = DateTimeOffset.Parse("2026-10-18T01:00:00Z");
            var payment = OfficialWebhook(
                "WH-OPERATIONS-DISABLED",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-OPERATIONS-DISABLED\",\"state\":\"completed\",\"billing_agreement_id\":\"I-OPERATIONS\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:operations-disabled"),
                payment,
                "P-DCX300",
                received));
            var source = new DurableCareOperationsSource(
                repository,
                timeProvider: new FixedTimeProvider(received));

            var health = await source.RefreshAsync();

            Assert.Equal("retry_due", health.StatusCode);
            Assert.Equal(1, health.Due);
            Assert.False(health.CanProcessDue);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ProcessDueAsync(health, explicitlyApproved: true));
            Assert.Contains("capability is disabled", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OPERATIONS-DISABLED"))!.AttemptCount);
            Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task DurableOperationsSource_RequiresApprovalAndExactReviewThenReplaysLocallyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-care-operations-reviewed-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";
            var received = DateTimeOffset.Parse("2026-10-18T02:00:00Z");
            var firstPayment = OfficialWebhook(
                "WH-OPERATIONS-FIRST",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-OPERATIONS-FIRST\",\"state\":\"completed\",\"billing_agreement_id\":\"I-OPERATIONS\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            var secondPayment = OfficialWebhook(
                "WH-OPERATIONS-SECOND",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-OPERATIONS-SECOND\",\"state\":\"completed\",\"billing_agreement_id\":\"I-OPERATIONS\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            var activation = OfficialWebhook(
                "WH-OPERATIONS-ACTIVE",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-OPERATIONS\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}");
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:operations-first"),
                firstPayment,
                planId,
                received));
            var source = new DurableCareOperationsSource(
                repository,
                processingCapabilityEnabled: true,
                timeProvider: new FixedTimeProvider(received));
            var firstReview = await source.RefreshAsync();
            Assert.True(firstReview.CanProcessDue);

            var approvalError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ProcessDueAsync(firstReview, explicitlyApproved: false));
            Assert.Contains("Explicit approval", approvalError.Message, StringComparison.Ordinal);
            Assert.Equal(0, (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OPERATIONS-FIRST"))!.AttemptCount);

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:operations-second"),
                secondPayment,
                planId,
                received));
            var staleError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ProcessDueAsync(firstReview, explicitlyApproved: true));
            Assert.Contains("state changed", staleError.Message, StringComparison.Ordinal);
            Assert.Equal(0, (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OPERATIONS-FIRST"))!.AttemptCount);
            Assert.Equal(0, (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OPERATIONS-SECOND"))!.AttemptCount);

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:operations-active"),
                activation,
                planId,
                received));
            Assert.Equal(6, handler.Calls);
            var exactReview = await source.RefreshAsync();
            var concurrentAttempts = new[]
            {
                source.ProcessDueAsync(exactReview, explicitlyApproved: true),
                source.ProcessDueAsync(exactReview, explicitlyApproved: true),
            };
            var successes = new List<CareOperationsProcessResult>();
            var failures = new List<InvalidOperationException>();
            foreach (var attempt in concurrentAttempts)
            {
                try
                {
                    successes.Add(await attempt);
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception);
                }
            }

            var result = Assert.Single(successes);
            Assert.Contains(
                "state changed",
                Assert.Single(failures).Message,
                StringComparison.Ordinal);

            Assert.Equal(2, result.Scanned);
            Assert.Equal(2, result.Completed);
            Assert.Equal(0, result.StillPending);
            Assert.Equal(0, result.Blocked);
            Assert.Equal(0, result.ProviderCalls);
            Assert.Equal("clear", result.Health.StatusCode);
            Assert.False(result.Health.CanProcessDue);
            Assert.Equal(600, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(6, handler.Calls);

            var replayError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.ProcessDueAsync(exactReview, explicitlyApproved: true));
            Assert.Contains("state changed", replayError.Message, StringComparison.Ordinal);
            Assert.Equal(600, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(6, handler.Calls);
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

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
    public async Task VerifiedSubscriptionWebhooks_BindPlanAndSaleAndCapCorrections()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-subscription-{Guid.NewGuid():N}");
        try
        {
            var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
            {
                if (index % 2 == 0)
                {
                    await AssertTokenRequestAsync(request, cancellationToken);
                    return Json(request, HttpStatusCode.OK, TokenResponse());
                }

                Assert.Equal(
                    "/v1/notifications/verify-webhook-signature",
                    request.RequestUri!.AbsolutePath);
                using var document = JsonDocument.Parse(
                    await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                Assert.StartsWith(
                    "WH-",
                    document.RootElement.GetProperty("webhook_event").GetProperty("id").GetString(),
                    StringComparison.Ordinal);
                return Json(request, HttpStatusCode.OK, "{\"verification_status\":\"SUCCESS\"}");
            });
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";

            var activation = OfficialWebhook(
                "WH-ACTIVE",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-DCX-ONE\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}");
            var payment = OfficialWebhook(
                "WH-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-DCX-ONE\",\"state\":\"completed\",\"billing_agreement_id\":\"I-DCX-ONE\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            var refund = OfficialWebhook(
                "WH-REFUND",
                "PAYMENT.SALE.REFUNDED",
                "refund",
                "{\"id\":\"REFUND-DCX-ONE\",\"state\":\"completed\",\"sale_id\":\"SALE-DCX-ONE\",\"amount\":{\"total\":\"1.00\",\"currency\":\"USD\"}}");

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:active"), activation, planId));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:payment"), payment, planId));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:refund"), refund, planId));
            repository = await DurableCareRepository.OpenAsync(root);
            Assert.False(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:refund"), refund, planId));
            Assert.Equal(200, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.False((await repository.GetEntitlementAsync("I-DCX-ONE"))!.Active);

            var excessive = OfficialWebhook(
                "WH-REVERSAL",
                "PAYMENT.SALE.REVERSED",
                "refund",
                "{\"id\":\"REVERSAL-DCX-ONE\",\"state\":\"completed\",\"sale_id\":\"SALE-DCX-ONE\",\"amount\":{\"total\":\"2.50\",\"currency\":\"USD\"}}");
            var excessiveError = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                repository.ProcessOfficialSandboxWebhookAsync(
                    adapter, Evidence("transmission:reversal"), excessive, planId));
            Assert.Equal("paypal_sandbox_correction_invalid", excessiveError.Code);
            Assert.Equal(200, await repository.BalanceAsync(LedgerAccount.Cash));

            var validRemainder = OfficialWebhook(
                "WH-REVERSAL",
                "PAYMENT.SALE.REVERSED",
                "refund",
                "{\"id\":\"REVERSAL-DCX-ONE\",\"state\":\"completed\",\"sale_id\":\"SALE-DCX-ONE\",\"amount\":{\"total\":\"2.00\",\"currency\":\"USD\"}}");
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:reversal"), validRemainder, planId));
            Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.True(await repository.ReconcilesAsync());

            var wrongPlan = OfficialWebhook(
                "WH-WRONG-PLAN",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-DCX-WRONG\",\"plan_id\":\"P-OTHER300\",\"status\":\"ACTIVE\"}");
            var planError = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                repository.ProcessOfficialSandboxWebhookAsync(
                    adapter, Evidence("transmission:wrong-plan"), wrongPlan, planId));
            Assert.Equal("paypal_sandbox_subscription_plan_mismatch", planError.Code);
            Assert.Null(await repository.GetEntitlementAsync("I-DCX-WRONG"));
            Assert.Null(await repository.GetOfficialWebhookQuarantineAsync("WH-WRONG-PLAN"));
            Assert.Equal(14, handler.Calls);
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task VerifiedOutOfOrderWebhooks_QuarantineThenRecoverLocallyInCausalOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-quarantine-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";
            var received = DateTimeOffset.Parse("2026-10-17T02:00:00Z");
            var payment = OfficialWebhook(
                "WH-OUT-OF-ORDER-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-OUT-OF-ORDER\",\"state\":\"completed\",\"billing_agreement_id\":\"I-OUT-OF-ORDER\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}",
                "2026-10-17T01:03:00Z");
            var refund = OfficialWebhook(
                "WH-OUT-OF-ORDER-REFUND",
                "PAYMENT.SALE.REFUNDED",
                "refund",
                "{\"id\":\"REFUND-OUT-OF-ORDER\",\"state\":\"completed\",\"sale_id\":\"SALE-OUT-OF-ORDER\",\"amount\":{\"total\":\"1.00\",\"currency\":\"USD\"}}",
                "2026-10-17T01:04:00Z");
            var activation = OfficialWebhook(
                "WH-OUT-OF-ORDER-ACTIVE",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-OUT-OF-ORDER\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}",
                "2026-10-17T01:02:00Z");

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:out-of-order-payment"), payment, planId, received));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:out-of-order-refund"), refund, planId, received));
            Assert.Equal("pending", (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OUT-OF-ORDER-PAYMENT"))!.State);
            Assert.Equal("pending", (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OUT-OF-ORDER-REFUND"))!.State);
            Assert.Equal(
                new WebhookQuarantineHealthSnapshot(
                    "retry_due", 2, 2, 0, 0, 0, received, received),
                await repository.GetOfficialWebhookQuarantineHealthAsync(received));
            Assert.Null(await repository.GetWebhookAuditAsync("WH-OUT-OF-ORDER-PAYMENT"));
            Assert.Null(await repository.GetWebhookAuditAsync("WH-OUT-OF-ORDER-REFUND"));
            repository = await DurableCareRepository.OpenAsync(root);

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:out-of-order-active"), activation, planId, received));
            Assert.Equal(6, handler.Calls);
            var summary = await repository.ReprocessQuarantinedOfficialWebhooksAsync(received);
            Assert.Equal(new WebhookQuarantineReprocessSummary(2, 2, 0, 0), summary);
            Assert.Equal(6, handler.Calls);
            Assert.NotNull(await repository.GetWebhookAuditAsync("WH-OUT-OF-ORDER-PAYMENT"));
            Assert.NotNull(await repository.GetWebhookAuditAsync("WH-OUT-OF-ORDER-REFUND"));
            Assert.Equal("completed", (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OUT-OF-ORDER-PAYMENT"))!.State);
            Assert.Equal("completed", (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-OUT-OF-ORDER-REFUND"))!.State);
            Assert.Equal(
                new WebhookQuarantineHealthSnapshot(
                    "clear", 0, 0, 0, 2, 1, null, null),
                await repository.GetOfficialWebhookQuarantineHealthAsync(received));
            Assert.Equal(200, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.False((await repository.GetEntitlementAsync("I-OUT-OF-ORDER"))!.Active);
            Assert.True(await repository.ReconcilesAsync());

            Assert.False(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:out-of-order-refund"), refund, planId, received));
            Assert.Equal(8, handler.Calls);
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task MissingDependencyRetry_UsesBoundedBackoffAndNoProviderCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-backoff-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";
            var received = DateTimeOffset.Parse("2026-10-17T03:00:00Z");
            var payment = OfficialWebhook(
                "WH-BACKOFF-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-BACKOFF\",\"state\":\"completed\",\"billing_agreement_id\":\"I-BACKOFF\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            var activation = OfficialWebhook(
                "WH-BACKOFF-ACTIVE",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-BACKOFF\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}");

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:backoff-payment"), payment, planId, received));
            Assert.Equal(
                new WebhookQuarantineReprocessSummary(1, 0, 1, 0),
                await repository.ReprocessQuarantinedOfficialWebhooksAsync(received));
            var pending = (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-BACKOFF-PAYMENT"))!;
            Assert.Equal(1, pending.AttemptCount);
            Assert.Equal(received.AddSeconds(2), pending.NextAttemptUtc);
            Assert.Equal(
                new WebhookQuarantineHealthSnapshot(
                    "retry_scheduled", 1, 0, 0, 0, 1, received, received.AddSeconds(2)),
                await repository.GetOfficialWebhookQuarantineHealthAsync(received.AddSeconds(1)));
            Assert.Equal(
                new WebhookQuarantineReprocessSummary(0, 0, 0, 0),
                await repository.ReprocessQuarantinedOfficialWebhooksAsync(received.AddSeconds(1)));
            Assert.Equal(2, handler.Calls);

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:backoff-active"),
                activation,
                planId,
                received.AddSeconds(1)));
            Assert.Equal(4, handler.Calls);
            Assert.Equal(
                new WebhookQuarantineReprocessSummary(1, 1, 0, 0),
                await repository.ReprocessQuarantinedOfficialWebhooksAsync(received.AddSeconds(2)));
            Assert.Equal(4, handler.Calls);
            Assert.Equal(300, await repository.BalanceAsync(LedgerAccount.Cash));
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task QuarantinedCorrection_BlocksWhenLaterStateMakesItInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-quarantine-blocked-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";
            var received = DateTimeOffset.Parse("2026-10-17T03:30:00Z");
            var delayedRefund = OfficialWebhook(
                "WH-BLOCKED-REFUND",
                "PAYMENT.SALE.REFUNDED",
                "refund",
                "{\"id\":\"REFUND-BLOCKED\",\"state\":\"completed\",\"sale_id\":\"SALE-BLOCKED\",\"amount\":{\"total\":\"2.50\",\"currency\":\"USD\"}}",
                "2026-10-17T01:05:00Z");
            var activation = OfficialWebhook(
                "WH-BLOCKED-ACTIVE",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-BLOCKED\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}");
            var payment = OfficialWebhook(
                "WH-BLOCKED-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-BLOCKED\",\"state\":\"completed\",\"billing_agreement_id\":\"I-BLOCKED\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");
            var acceptedRefund = OfficialWebhook(
                "WH-BLOCKED-ACCEPTED-REFUND",
                "PAYMENT.SALE.REFUNDED",
                "refund",
                "{\"id\":\"REFUND-ACCEPTED\",\"state\":\"completed\",\"sale_id\":\"SALE-BLOCKED\",\"amount\":{\"total\":\"1.00\",\"currency\":\"USD\"}}");

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:blocked-refund"), delayedRefund, planId, received));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:blocked-active"), activation, planId, received));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:blocked-payment"), payment, planId, received));
            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter,
                Evidence("transmission:blocked-accepted-refund"),
                acceptedRefund,
                planId,
                received));
            Assert.Equal(8, handler.Calls);

            Assert.Equal(
                new WebhookQuarantineReprocessSummary(1, 0, 0, 1),
                await repository.ReprocessQuarantinedOfficialWebhooksAsync(received));
            Assert.Equal(8, handler.Calls);
            var blocked = (await repository.GetOfficialWebhookQuarantineAsync(
                "WH-BLOCKED-REFUND"))!;
            Assert.Equal("blocked", blocked.State);
            Assert.Equal("paypal_sandbox_quarantine_reprocess_blocked", blocked.ReasonCode);
            Assert.Equal(1, blocked.AttemptCount);
            Assert.Null(blocked.NextAttemptUtc);
            Assert.Equal(
                new WebhookQuarantineHealthSnapshot(
                    "operator_action_required", 0, 0, 1, 0, 1, null, null),
                await repository.GetOfficialWebhookQuarantineHealthAsync(received));
            Assert.Null(await repository.GetWebhookAuditAsync("WH-BLOCKED-REFUND"));
            Assert.Equal(200, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(
                new WebhookQuarantineReprocessSummary(0, 0, 0, 0),
                await repository.ReprocessQuarantinedOfficialWebhooksAsync(received.AddDays(1)));
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task QuarantinedWebhook_ReplayConflictsAndStoredTamperFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-paypal-quarantine-tamper-{Guid.NewGuid():N}");
        try
        {
            var handler = VerifiedWebhookHandler();
            using var adapter = Adapter(handler);
            var repository = await DurableCareRepository.OpenAsync(root);
            const string planId = "P-DCX300";
            var received = DateTimeOffset.Parse("2026-10-17T04:00:00Z");
            var payment = OfficialWebhook(
                "WH-TAMPER-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-TAMPER\",\"state\":\"completed\",\"billing_agreement_id\":\"I-TAMPER\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}");

            Assert.True(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:tamper-payment"), payment, planId, received));
            var localFixtureKey = Enumerable.Repeat((byte)19, 32).ToArray();
            var crossPathFixture = new PayPalSandboxWebhookEvent(
                "WH-TAMPER-PAYMENT",
                "transmission:tamper-payment",
                "PAYMENT.SALE.COMPLETED",
                "I-TAMPER",
                300,
                "USD",
                DateTimeOffset.Parse("2026-10-17T01:02:03Z"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ProcessSandboxWebhookAsync(
                    crossPathFixture,
                    PayPalSandboxWebhookIntegrity.Sign(crossPathFixture, localFixtureKey),
                    localFixtureKey));
            Assert.False(await repository.ProcessOfficialSandboxWebhookAsync(
                adapter, Evidence("transmission:tamper-payment"), payment, planId, received));
            var conflicting = OfficialWebhook(
                "WH-TAMPER-PAYMENT",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-TAMPER\",\"state\":\"completed\",\"billing_agreement_id\":\"I-TAMPER\",\"amount\":{\"total\":\"3.00\",\"currency\":\"USD\"}}",
                "2026-10-17T01:02:04Z");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ProcessOfficialSandboxWebhookAsync(
                    adapter, Evidence("transmission:tamper-payment"), conflicting, planId, received));

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "care-ledger.sqlite3"),
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE webhook_event_quarantine
                    SET verified_event_sha256 = $tampered
                    WHERE event_id = 'WH-TAMPER-PAYMENT';
                    """;
                command.Parameters.AddWithValue("$tampered", new string('0', 64));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var tamperError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ReprocessQuarantinedOfficialWebhooksAsync(received));
            Assert.Contains("failed its stored digest", tamperError.Message, StringComparison.Ordinal);
            Assert.Null(await repository.GetWebhookAuditAsync("WH-TAMPER-PAYMENT"));
            Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
            Assert.Equal(6, handler.Calls);
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    [Fact]
    public async Task VerifyAndMapWebhook_FailsClosedBeforeOrAfterVerificationAsApplicable()
    {
        var noNetwork = new ScriptedHandler((_, request, _) =>
            Task.FromResult(Json(request, HttpStatusCode.InternalServerError, "{}")));
        using (var adapter = Adapter(noNetwork))
        {
            var malformed = OfficialWebhook(
                "WH-BAD",
                "PAYMENT.SALE.COMPLETED",
                "sale",
                "{\"id\":\"SALE-BAD\",\"state\":\"completed\",\"billing_agreement_id\":\"I-BAD\",\"amount\":{\"total\":\"3e0\",\"currency\":\"USD\"}}");
            var invalid = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.VerifyAndMapWebhookAsync(Evidence(), malformed));
            Assert.Equal("paypal_sandbox_webhook_resource_invalid", invalid.Code);
            Assert.Equal(0, noNetwork.Calls);
        }

        var rejected = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            return Json(request, HttpStatusCode.OK, "{\"verification_status\":\"FAILURE\"}");
        });
        using (var adapter = Adapter(rejected))
        {
            var valid = OfficialWebhook(
                "WH-UNVERIFIED",
                "BILLING.SUBSCRIPTION.ACTIVATED",
                "subscription",
                "{\"id\":\"I-UNVERIFIED\",\"plan_id\":\"P-DCX300\",\"status\":\"ACTIVE\"}");
            var unverified = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
                adapter.VerifyAndMapWebhookAsync(Evidence(), valid));
            Assert.Equal("paypal_sandbox_webhook_not_verified", unverified.Code);
            Assert.Equal(2, rejected.Calls);
        }
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
    public async Task CompletedBatch_RecordsFailedBoundItemWithoutPostingSuccess()
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

        var lookup = await adapter.LookupSandboxAsync(
            Preview("batch:partial", 500), CancellationToken.None);

        Assert.Equal(SandboxPayoutLookupState.Failed, lookup.State);
        var item = Assert.Single(lookup.ItemResults!);
        Assert.False(item.Succeeded);
        Assert.Equal("FAILED", item.ProviderStatus);
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
    public async Task RecipientSnapshotSubstitution_IsDeniedBeforeCredentialUse()
    {
        var handler = new ScriptedHandler((_, request, _) =>
            Task.FromResult(Json(request, HttpStatusCode.OK, TokenResponse())));
        using var adapter = Adapter(
            handler,
            new StaticRecipientDirectory(new PayPalSandboxRecipient(
                DefaultSnapshotId,
                DefaultContributor,
                "changed-recipient@personal.example.com",
                true)));

        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            adapter.SendSandboxAsync(
                Preview("batch:recipient-substitution", 500),
                CancellationToken.None));

        Assert.Equal("paypal_sandbox_recipient_snapshot_invalid", exception.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CompletedBatch_ReturnsExactMixedItemOutcomes()
    {
        var store = new MemoryReferenceStore();
        await store.StoreProviderBatchIdAsync("batch:mixed-items", "PB-MIXED");
        var handler = new ScriptedHandler(async (index, request, cancellationToken) =>
        {
            if (index == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            return Json(request, HttpStatusCode.OK, MixedPayoutLookupResponse());
        });
        using var adapter = Adapter(handler, store: store);
        var preview = new PayoutBatchPreview(
            "batch:mixed-items",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [
                new PayoutItem(
                    DefaultContributor,
                    500,
                    PayoutRecipientApproval.Create(DefaultSnapshotId, DefaultEmail)),
                new PayoutItem(
                    "contributor:two",
                    700,
                    PayoutRecipientApproval.Create(
                        "snapshot:contributor-two",
                        "contributor-two@personal.example.com")),
            ]);

        var lookup = await adapter.LookupSandboxAsync(preview, CancellationToken.None);

        Assert.Equal(SandboxPayoutLookupState.PartiallySucceeded, lookup.State);
        Assert.Collection(
            lookup.ItemResults!,
            item =>
            {
                Assert.Equal(DefaultContributor, item.ContributorPseudonym);
                Assert.True(item.Succeeded);
                Assert.Equal(500, item.AmountCents);
            },
            item =>
            {
                Assert.Equal("contributor:two", item.ContributorPseudonym);
                Assert.False(item.Succeeded);
                Assert.Equal("FAILED", item.ProviderStatus);
                Assert.Equal(700, item.AmountCents);
            });
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
                DefaultSnapshotId,
                DefaultContributor,
                DefaultEmail,
                false)),
            new MemoryReferenceStore());
        var exception = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            ineligible.SendSandboxAsync(Preview("batch:ineligible", 500), CancellationToken.None));
        Assert.Equal("paypal_sandbox_recipient_snapshot_invalid", exception.Code);
        Assert.Equal(0, handler.Calls);

        var duplicateDirectory = new StaticRecipientDirectory(
            new PayPalSandboxRecipient(
                "snapshot:one",
                "contributor:one",
                "same@personal.example.com",
                true));
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
            [
                new PayoutItem(
                    "contributor:one",
                    100,
                    PayoutRecipientApproval.Create("snapshot:one", "same@personal.example.com")),
                new PayoutItem(
                    "contributor:two",
                    200,
                    PayoutRecipientApproval.Create("snapshot:two", "same@personal.example.com")),
            ]);
        var duplicateException = await Assert.ThrowsAsync<PayPalSandboxException>(() =>
            duplicate.SendSandboxAsync(duplicatePreview, CancellationToken.None));
        Assert.Equal("paypal_sandbox_recipient_snapshot_invalid", duplicateException.Code);
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
                DefaultSnapshotId,
                DefaultContributor,
                DefaultEmail,
                true)),
            store ?? new MemoryReferenceStore());

    private static PayoutBatchPreview Preview(string batchId, long cents) => new(
        batchId,
        PayoutExecutionMode.Sandbox,
        new DateOnly(2026, 4, 1),
        [new PayoutItem(
            DefaultContributor,
            cents,
            PayoutRecipientApproval.Create(DefaultSnapshotId, DefaultEmail))]);

    private static PayPalSandboxWebhookVerificationEvidence Evidence(
        string transmissionId = "transmission:one") => new(
        "SHA256withRSA",
        new Uri("https://api.sandbox.paypal.com/v1/notifications/certs/CERT-DCX123"),
        transmissionId,
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 64).ToArray()),
        "2026-10-17T01:02:03Z");

    private static byte[] OfficialWebhook(
        string eventId,
        string eventType,
        string resourceType,
        string resourceJson,
        string createTime = "2026-10-17T01:02:03Z") => Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{eventId}\",\"create_time\":\"{createTime}\",\"resource_type\":\"{resourceType}\",\"event_type\":\"{eventType}\",\"resource\":{resourceJson}}}");

    private static ScriptedHandler VerifiedWebhookHandler() => new(
        async (index, request, cancellationToken) =>
        {
            if (index % 2 == 0)
            {
                await AssertTokenRequestAsync(request, cancellationToken);
                return Json(request, HttpStatusCode.OK, TokenResponse());
            }

            Assert.Equal(
                "/v1/notifications/verify-webhook-signature",
                request.RequestUri!.AbsolutePath);
            return Json(request, HttpStatusCode.OK, "{\"verification_status\":\"SUCCESS\"}");
        });

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

    private static string MixedPayoutLookupResponse() =>
        """
        {"total_items":2,"total_pages":1,"batch_header":{"payout_batch_id":"PB-MIXED","batch_status":"SUCCESS","sender_batch_header":{"sender_batch_id":"batch:mixed-items"}},"items":[{"payout_item_id":"PI-000001","payout_batch_id":"PB-MIXED","transaction_status":"SUCCESS","payout_item":{"amount":{"currency":"USD","value":"5.00"},"receiver":"contributor-one@personal.example.com","sender_item_id":"dcx-000001"}},{"payout_item_id":"PI-000002","payout_batch_id":"PB-MIXED","transaction_status":"FAILED","payout_item":{"amount":{"currency":"USD","value":"7.00"},"receiver":"contributor-two@personal.example.com","sender_item_id":"dcx-000002"}}]}
        """;

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
            string snapshotId,
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
