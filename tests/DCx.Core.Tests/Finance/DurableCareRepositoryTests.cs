using System.Security.Cryptography;
using DCx.Core.Finance;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Tests.Finance;

public sealed class DurableCareRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-care-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-01-15T00:00:00Z");

    [Fact]
    public async Task WebhooksAndLedger_RemainIdempotentAndBalancedAcrossReopen()
    {
        var key = Enumerable.Repeat((byte)31, 32).ToArray();
        var repository = await DurableCareRepository.OpenAsync(root);
        var activated = Event("event:active", "transmission:active", "BILLING.SUBSCRIPTION.ACTIVATED", 0);
        var payment = Event("event:payment", "transmission:payment", "PAYMENT.SALE.COMPLETED", 300);

        Assert.True(await repository.ProcessSandboxWebhookAsync(
            activated, PayPalSandboxWebhookIntegrity.Sign(activated, key), key));
        Assert.True(await repository.ProcessSandboxWebhookAsync(
            payment, PayPalSandboxWebhookIntegrity.Sign(payment, key), key));
        Assert.True(await repository.ReconcilesAsync());
        Assert.Equal(300, await repository.BalanceAsync(LedgerAccount.Cash));

        var reopened = await DurableCareRepository.OpenAsync(root);
        Assert.False(await reopened.ProcessSandboxWebhookAsync(
            payment, PayPalSandboxWebhookIntegrity.Sign(payment, key), key));
        Assert.True((await reopened.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(300, await reopened.BalanceAsync(LedgerAccount.Cash));

        var refund = Event("event:refund", "transmission:refund", "PAYMENT.SALE.REFUNDED", 300);
        Assert.True(await reopened.ProcessSandboxWebhookAsync(
            refund, PayPalSandboxWebhookIntegrity.Sign(refund, key), key));
        Assert.False((await reopened.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task WebhookCausality_RejectsStaleReactivationAndRecordsMinimizedAuditAcrossReopen()
    {
        var key = Enumerable.Repeat((byte)41, 32).ToArray();
        var repository = await DurableCareRepository.OpenAsync(root);
        var activated = Event("event:causal-active", "transmission:causal-active",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now };
        var cancelled = Event("event:causal-cancel", "transmission:causal-cancel",
            "BILLING.SUBSCRIPTION.CANCELLED", 0) with { OccurredUtc = now.AddMinutes(10) };
        await ProcessAsync(repository, activated, key);
        await ProcessAsync(repository, cancelled, key);

        var reopened = await DurableCareRepository.OpenAsync(root);
        var staleActivation = Event("event:causal-stale", "transmission:causal-stale",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now.AddMinutes(5) };
        var equalActivation = Event("event:causal-equal", "transmission:causal-equal",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now.AddMinutes(10) };
        Assert.True(await ProcessAsync(reopened, staleActivation, key));
        Assert.True(await ProcessAsync(reopened, equalActivation, key));
        Assert.False(await ProcessAsync(reopened, equalActivation, key));

        var entitlement = (await reopened.GetEntitlementAsync("subscription:one"))!;
        Assert.False(entitlement.Active);
        Assert.Equal(cancelled.OccurredUtc, entitlement.UpdatedUtc);
        var staleAudit = (await reopened.GetWebhookAuditAsync(staleActivation.EventId))!;
        Assert.Equal(PayPalSandboxWebhookPolicy.EntitlementStaleIgnored, staleAudit.DecisionCode);
        Assert.Equal(64, staleAudit.SubscriptionSha256.Length);
        Assert.Equal(64, staleAudit.PayloadSha256.Length);
        Assert.DoesNotContain("subscription:one", staleAudit.SubscriptionSha256, StringComparison.Ordinal);
        var equalAudit = (await reopened.GetWebhookAuditAsync(equalActivation.EventId))!;
        Assert.Equal(PayPalSandboxWebhookPolicy.EntitlementEqualIgnored, equalAudit.DecisionCode);

        var reactivated = Event("event:causal-new", "transmission:causal-new",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now.AddMinutes(20) };
        var staleRefund = Event("event:causal-old-refund", "transmission:causal-old-refund",
            "PAYMENT.SALE.REFUNDED", 300) with { OccurredUtc = now.AddMinutes(15) };
        var sameTimeCancellation = Event("event:causal-tie-cancel", "transmission:causal-tie-cancel",
            "BILLING.SUBSCRIPTION.CANCELLED", 0) with { OccurredUtc = now.AddMinutes(20) };
        await ProcessAsync(reopened, reactivated, key);
        await ProcessAsync(reopened, staleRefund, key);
        Assert.True((await reopened.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(
            PayPalSandboxWebhookPolicy.EntitlementStaleIgnored,
            (await reopened.GetWebhookAuditAsync(staleRefund.EventId))!.DecisionCode);
        Assert.Equal(-300, await reopened.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await reopened.ReconcilesAsync());
        await ProcessAsync(reopened, sameTimeCancellation, key);
        Assert.False((await reopened.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(
            PayPalSandboxWebhookPolicy.EntitlementAppliedEqualDeactivation,
            (await reopened.GetWebhookAuditAsync(sameTimeCancellation.EventId))!.DecisionCode);
    }

    [Fact]
    public async Task LegacySchema_MigratesWithoutReactivatingNewerEntitlement()
    {
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "care-ledger.sqlite3");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE webhook_events(
                    event_id TEXT PRIMARY KEY,
                    transmission_id TEXT NOT NULL UNIQUE,
                    event_type TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL,
                    occurred_utc TEXT NOT NULL
                );
                CREATE TABLE entitlements(
                    subscription_id TEXT PRIMARY KEY,
                    active INTEGER NOT NULL CHECK(active IN (0, 1)),
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO webhook_events(
                    event_id, transmission_id, event_type, payload_sha256, occurred_utc)
                VALUES('event:legacy', 'transmission:legacy',
                    'BILLING.SUBSCRIPTION.CANCELLED', 'legacy-digest', $updated);
                INSERT INTO entitlements(subscription_id, active, updated_utc)
                VALUES('subscription:one', 0, $updated);
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$updated", now.AddMinutes(10).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = await DurableCareRepository.OpenAsync(root);
        var legacyAudit = (await repository.GetWebhookAuditAsync("event:legacy"))!;
        Assert.Equal("legacy_unclassified", legacyAudit.DecisionCode);
        Assert.Equal(string.Empty, legacyAudit.SubscriptionSha256);
        var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var stale = Event("event:post-migration", "transmission:post-migration",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now };
        Assert.True(await ProcessAsync(repository, stale, key));
        Assert.False((await repository.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(
            PayPalSandboxWebhookPolicy.EntitlementStaleIgnored,
            (await repository.GetWebhookAuditAsync(stale.EventId))!.DecisionCode);

        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        var versionCommand = reopened.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Ledger_RejectsConflictingEventAcrossReopen()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var entry = new LedgerJournalEntry(
            "event:cost",
            now,
            "approved_cost",
            [
                new LedgerPosting(LedgerAccount.ApprovedOperatingCosts, 100, 0),
                new LedgerPosting(LedgerAccount.Cash, 0, 100),
            ]);
        Assert.True(await repository.PostAsync(entry));
        Assert.False(await repository.PostAsync(entry));

        var reopened = await DurableCareRepository.OpenAsync(root);
        var conflicting = entry with
        {
            Postings =
            [
                new LedgerPosting(LedgerAccount.ApprovedOperatingCosts, 101, 0),
                new LedgerPosting(LedgerAccount.Cash, 0, 101),
            ],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PostAsync(conflicting));
        Assert.Equal(100, await reopened.BalanceAsync(LedgerAccount.ApprovedOperatingCosts));
    }

    [Fact]
    public async Task PayoutReservationAndTerminalResult_SurviveRestartAndPreventDuplicate()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var care = CareAllocationCalculator.Calculate(
            new CarePeriodInput(2026, 1, 10_000, 0, 0, 0, 0, MinimumPayoutCents: 100),
            [new ContributorUsageWeight("contributor:one", 1m)]);
        await repository.PostAsync(CareLedgerJournal.ContributorAccrual("care:2026-01", care, now));
        var preview = PayoutBatchPreviewFactory.CreateSandbox("batch:durable", care);
        var provider = new FixtureProvider(true);

        var result = await new DurablePayoutCoordinator(repository, provider).ExecuteAsync(
            preview, true, true, care.Net60EligibilityDate, now);

        Assert.True(result.Succeeded);
        Assert.Equal("succeeded", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.True(await repository.ReconcilesAsync());

        var reopened = await DurableCareRepository.OpenAsync(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DurablePayoutCoordinator(reopened, provider).ExecuteAsync(
            preview, true, true, care.Net60EligibilityDate, now.AddMinutes(1)));
        Assert.Equal(1, provider.Calls);
        Assert.Equal("succeeded", (await reopened.GetPayoutStateAsync(preview.BatchId))!.Status);
    }

    [Fact]
    public async Task FailedSandboxPayout_IsDurablyReconciledWithoutCashPosting()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = new PayoutBatchPreview(
            "batch:failed",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [new PayoutItem("contributor:one", 500)]);
        var result = await new DurablePayoutCoordinator(repository, new FixtureProvider(false)).ExecuteAsync(
            preview, true, true, new DateOnly(2026, 4, 1), now);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
    }

    [Fact]
    public async Task RestartReconciliation_FinalizesProviderSuccessWithoutResending()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:accepted-before-crash");
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var provider = new ReconcilableFixtureProvider();
        provider.SetLookup(preview.BatchId, SandboxPayoutLookupState.Succeeded, "SANDBOX_SUCCESS");

        var reopened = await DurableCareRepository.OpenAsync(root);
        var summary = await new DurablePayoutCoordinator(reopened, provider).ReconcilePendingAsync(
            now.AddMinutes(1), now.AddMinutes(1));

        Assert.Equal(new PayoutReconciliationSummary(1, 1, 0, 0), summary);
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal("succeeded", (await reopened.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task RestartReconciliation_ResubmitsOnlyProviderNotFoundBatchIdempotently()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:not-sent-before-crash");
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var provider = new ReconcilableFixtureProvider();

        var summary = await new DurablePayoutCoordinator(repository, provider).ReconcilePendingAsync(
            now.AddMinutes(1), now.AddMinutes(1));
        var second = await new DurablePayoutCoordinator(repository, provider).ReconcilePendingAsync(
            now.AddMinutes(2), now.AddMinutes(2));

        Assert.Equal(new PayoutReconciliationSummary(1, 1, 0, 1), summary);
        Assert.Equal(new PayoutReconciliationSummary(0, 0, 0, 0), second);
        Assert.Equal(1, provider.SendCalls);
        Assert.Equal("succeeded", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
    }

    [Fact]
    public async Task RestartReconciliation_LeavesProviderPendingBatchReserved()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:provider-pending");
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var provider = new ReconcilableFixtureProvider();
        provider.SetLookup(preview.BatchId, SandboxPayoutLookupState.Pending, "PENDING");

        var summary = await new DurablePayoutCoordinator(repository, provider).ReconcilePendingAsync(
            now.AddMinutes(1), now.AddMinutes(1));

        Assert.Equal(new PayoutReconciliationSummary(1, 0, 1, 0), summary);
        Assert.Equal("reserved", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Equal(0, provider.SendCalls);
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    private PayPalSandboxWebhookEvent Event(string eventId, string transmissionId, string eventType, long amount) =>
        new(eventId, transmissionId, eventType, "subscription:one", amount, "USD", now);

    private static Task<bool> ProcessAsync(
        DurableCareRepository repository,
        PayPalSandboxWebhookEvent webhook,
        byte[] key) =>
        repository.ProcessSandboxWebhookAsync(
            webhook,
            PayPalSandboxWebhookIntegrity.Sign(webhook, key),
            key);

    private static PayoutBatchPreview Preview(string batchId) => new(
        batchId,
        PayoutExecutionMode.Sandbox,
        new DateOnly(2026, 1, 31),
        [new PayoutItem("contributor:one", 500)]);

    private async Task AccrueAsync(DurableCareRepository repository, PayoutBatchPreview preview) =>
        await repository.PostAsync(new LedgerJournalEntry(
            $"accrual:{preview.BatchId}",
            now,
            "fixture_contributor_accrual",
            [
                new LedgerPosting(LedgerAccount.ContributorExpense, preview.TotalCents, 0),
                new LedgerPosting(LedgerAccount.ContributorPayable, 0, preview.TotalCents),
            ]));

    private sealed class FixtureProvider(bool succeeds) : IPayoutSandboxProvider
    {
        public int Calls { get; private set; }

        public Task<PayoutBatchResult> SendSandboxAsync(PayoutBatchPreview preview, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PayoutBatchResult(
                preview.BatchId,
                succeeds,
                succeeds ? "SANDBOX_SUCCESS" : "SANDBOX_FAILED"));
        }
    }

    private sealed class ReconcilableFixtureProvider : IReconcilablePayoutSandboxProvider
    {
        private readonly Dictionary<string, SandboxPayoutLookupResult> lookups = new(StringComparer.Ordinal);

        public int SendCalls { get; private set; }

        public void SetLookup(string batchId, SandboxPayoutLookupState state, string? providerStatus) =>
            lookups[batchId] = new SandboxPayoutLookupResult(batchId, state, providerStatus);

        public Task<SandboxPayoutLookupResult> LookupSandboxAsync(
            string batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(lookups.GetValueOrDefault(batchId) ??
                            new SandboxPayoutLookupResult(batchId, SandboxPayoutLookupState.NotFound, null));

        public Task<PayoutBatchResult> SendSandboxAsync(
            PayoutBatchPreview preview,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            SetLookup(preview.BatchId, SandboxPayoutLookupState.Succeeded, "SANDBOX_SUCCESS");
            return Task.FromResult(new PayoutBatchResult(preview.BatchId, true, "SANDBOX_SUCCESS"));
        }
    }
}
