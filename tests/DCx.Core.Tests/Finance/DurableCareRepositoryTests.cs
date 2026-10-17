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

        var overrun = Event(
            "event:overrun", "transmission:overrun", "PAYMENT.SALE.REVERSED", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.ProcessSandboxWebhookAsync(
                overrun, PayPalSandboxWebhookIntegrity.Sign(overrun, key), key));
        Assert.Null(await reopened.GetWebhookAuditAsync(overrun.EventId));
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.Cash));
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
        var payment = Event("event:causal-payment", "transmission:causal-payment",
            "PAYMENT.SALE.COMPLETED", 300) with { OccurredUtc = now.AddMinutes(14) };
        var staleRefund = Event("event:causal-old-refund", "transmission:causal-old-refund",
            "PAYMENT.SALE.REFUNDED", 300) with { OccurredUtc = now.AddMinutes(15) };
        var sameTimeCancellation = Event("event:causal-tie-cancel", "transmission:causal-tie-cancel",
            "BILLING.SUBSCRIPTION.CANCELLED", 0) with { OccurredUtc = now.AddMinutes(20) };
        await ProcessAsync(reopened, payment, key);
        await ProcessAsync(reopened, reactivated, key);
        await ProcessAsync(reopened, staleRefund, key);
        Assert.True((await reopened.GetEntitlementAsync("subscription:one"))!.Active);
        Assert.Equal(
            PayPalSandboxWebhookPolicy.EntitlementStaleIgnored,
            (await reopened.GetWebhookAuditAsync(staleRefund.EventId))!.DecisionCode);
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.Cash));
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
        Assert.Equal(7L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FutureSchema_FailsClosedWithoutDowngrade()
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
            command.CommandText = "PRAGMA user_version = 8;";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DurableCareRepository.OpenAsync(root));
        Assert.Contains("newer than this application", exception.Message, StringComparison.Ordinal);

        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        var versionCommand = reopened.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaSix_AddsFailedItemRemediationLineageWithoutReplacingPriorState()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:schema-six-source");
        await AccrueAsync(repository, preview);
        await new DurablePayoutCoordinator(repository, new FixtureProvider(false)).ExecuteAsync(
            preview, true, true, preview.EligibilityDate, now);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE payout_item_remediations; PRAGMA user_version = 6;";
            await command.ExecuteNonQueryAsync();
        }

        var reopened = await DurableCareRepository.OpenAsync(root);
        Assert.Equal("failed", (await reopened.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Empty(await reopened.GetPayoutItemRemediationsAsync(preview.BatchId));

        await using var verified = new SqliteConnection(connectionString);
        await verified.OpenAsync();
        var version = verified.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, (long)(await version.ExecuteScalarAsync())!);
        var table = verified.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'payout_item_remediations';";
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WebhookIdentifiers_RejectConflictsWithoutPoisoningRejectedInput()
    {
        var key = Enumerable.Repeat((byte)43, 32).ToArray();
        var repository = await DurableCareRepository.OpenAsync(root);
        var original = Event(
            "event:identifier", "transmission:identifier", "PAYMENT.SALE.COMPLETED", 300);
        Assert.True(await ProcessAsync(repository, original, key));

        var reusedEvent = original with { TransmissionId = "transmission:different" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessAsync(repository, reusedEvent, key));
        var reusedTransmission = original with { EventId = "event:different" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessAsync(repository, reusedTransmission, key));

        var invalid = Event("event:correctable", "transmission:correctable",
            "PAYMENT.SALE.COMPLETED", 301);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(repository, invalid, key));
        Assert.True(await ProcessAsync(repository, invalid with { AmountCents = 300 }, key));
        Assert.Equal(600, await repository.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await repository.ReconcilesAsync());
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
        var preview = PayoutBatchPreviewFactory.CreateSandbox(
            "batch:durable",
            care,
            new Dictionary<string, PayoutRecipientApproval>
            {
                ["contributor:one"] = Approval("contributor:one"),
            });
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
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))]);
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

    [Fact]
    public async Task PendingSubmission_RemainsReservedUntilTerminalReconciliation()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:asynchronous-provider");
        var provider = new PendingFixtureProvider();

        var result = await new DurablePayoutCoordinator(repository, provider).ExecuteAsync(
            preview, true, true, preview.EligibilityDate, now);

        Assert.False(result.Succeeded);
        Assert.False(result.IsTerminal);
        Assert.Equal("PENDING", result.ProviderStatus);
        Assert.Equal("reserved", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CompletePayoutAsync(preview, result, now));
    }

    [Fact]
    public async Task ProviderBatchReference_IsConflictSafeAndSurvivesReopen()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:provider-reference");
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));

        await repository.StoreProviderBatchIdAsync(preview.BatchId, "PB-123456");
        await repository.StoreProviderBatchIdAsync(preview.BatchId, "PB-123456");
        var reopened = await DurableCareRepository.OpenAsync(root);

        Assert.Equal("PB-123456", await reopened.GetProviderBatchIdAsync(preview.BatchId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.StoreProviderBatchIdAsync(preview.BatchId, "PB-OTHER"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.StoreProviderBatchIdAsync("batch:not-reserved", "PB-NEW"));

        var second = Preview("batch:provider-reference-two");
        Assert.True(await reopened.ReservePayoutAsync(
            second, true, true, second.EligibilityDate, now));
        await Assert.ThrowsAsync<SqliteException>(() =>
            reopened.StoreProviderBatchIdAsync(second.BatchId, "PB-123456"));
    }

    [Fact]
    public async Task PartialPayout_PostsOnlySuccessfulItemsAndPersistsExactItemStates()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = new PayoutBatchPreview(
            "batch:partial-items",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [
                new PayoutItem("contributor:one", 500, Approval("contributor:one")),
                new PayoutItem("contributor:two", 700, Approval("contributor:two")),
            ]);
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var result = new PayoutBatchResult(
            preview.BatchId,
            PayoutBatchOutcome.PartiallySucceeded,
            "SUCCESS",
            [
                new PayoutItemResult("contributor:two", 700, false, "FAILED"),
                new PayoutItemResult("contributor:one", 500, true, "SUCCESS"),
            ]);

        Assert.True(await repository.CompletePayoutAsync(preview, result, now));
        var state = (await repository.GetPayoutStateAsync(preview.BatchId))!;
        Assert.Equal("partially_succeeded", state.Status);
        Assert.Equal(500, state.PaidCents);
        Assert.Equal(-700, await repository.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.Equal(-500, await repository.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await repository.ReconcilesAsync());

        var reopened = await DurableCareRepository.OpenAsync(root);
        var items = await reopened.GetPayoutItemStatesAsync(preview.BatchId);
        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("contributor:one", item.ContributorPseudonym);
                Assert.True(item.Succeeded);
                Assert.Equal(500, item.AmountCents);
                Assert.Equal(Approval("contributor:one").DestinationSha256, item.DestinationSha256);
            },
            item =>
            {
                Assert.Equal("contributor:two", item.ContributorPseudonym);
                Assert.False(item.Succeeded);
                Assert.Equal(700, item.AmountCents);
            });
        Assert.False(await reopened.CompletePayoutAsync(preview, result, now.AddMinutes(1)));
        var conflict = result with
        {
            ItemResults =
            [
                new PayoutItemResult("contributor:one", 500, false, "FAILED"),
                new PayoutItemResult("contributor:two", 700, false, "FAILED"),
            ],
            Outcome = PayoutBatchOutcome.Failed,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.CompletePayoutAsync(preview, conflict, now.AddMinutes(2)));
    }

    [Fact]
    public async Task TerminalReconciliation_ReturnsPaidItemsExactlyOnceWithoutResending()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = new PayoutBatchPreview(
            "batch:post-terminal-return",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [
                new PayoutItem("contributor:one", 500, Approval("contributor:one")),
                new PayoutItem("contributor:two", 700, Approval("contributor:two")),
            ]);
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var initial = new PayoutBatchResult(
            preview.BatchId,
            PayoutBatchOutcome.Succeeded,
            "SUCCESS",
            preview.Items.Select(item => new PayoutItemResult(
                item.ContributorPseudonym, item.AmountCents, true, "SUCCESS")).ToArray());
        Assert.True(await repository.CompletePayoutAsync(preview, initial, now));

        var provider = new ReconcilableFixtureProvider();
        provider.SetLookup(
            preview.BatchId,
            SandboxPayoutLookupState.PartiallySucceeded,
            "SUCCESS",
            [
                new PayoutItemResult("contributor:one", 500, false, "RETURNED"),
                new PayoutItemResult("contributor:two", 700, true, "SUCCESS"),
            ]);
        var reopened = await DurableCareRepository.OpenAsync(root);
        var coordinator = new DurablePayoutCoordinator(reopened, provider);
        var first = await coordinator.ReconcileTerminalAsync(now.AddMinutes(1), now.AddMinutes(1));

        Assert.Equal(new PayoutTerminalReconciliationSummary(1, 1, 0, 0), first);
        Assert.Equal(0, provider.SendCalls);
        var partial = (await reopened.GetPayoutStateAsync(preview.BatchId))!;
        Assert.Equal("partially_succeeded", partial.Status);
        Assert.Equal(700, partial.PaidCents);
        Assert.Equal(-700, await reopened.BalanceAsync(LedgerAccount.Cash));
        Assert.Equal(-500, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        var firstAdjustments = await reopened.GetPayoutItemAdjustmentsAsync(preview.BatchId);
        var firstAdjustment = Assert.Single(firstAdjustments);
        Assert.Equal("RETURNED", firstAdjustment.ToProviderStatus);
        Assert.Equal(500, firstAdjustment.AmountCents);
        Assert.True(await reopened.ReconcilesAsync());

        var again = await coordinator.ReconcileTerminalAsync(now.AddMinutes(2), now.AddMinutes(2));
        Assert.Equal(new PayoutTerminalReconciliationSummary(1, 0, 1, 0), again);
        Assert.Single(await reopened.GetPayoutItemAdjustmentsAsync(preview.BatchId));
        Assert.Equal(0, provider.SendCalls);

        provider.SetLookup(
            preview.BatchId,
            SandboxPayoutLookupState.Failed,
            "SUCCESS",
            [
                new PayoutItemResult("contributor:one", 500, false, "RETURNED"),
                new PayoutItemResult("contributor:two", 700, false, "REVERSED"),
            ]);
        var final = await coordinator.ReconcileTerminalAsync(now.AddMinutes(3), now.AddMinutes(3));

        Assert.Equal(new PayoutTerminalReconciliationSummary(1, 1, 0, 0), final);
        var failed = (await reopened.GetPayoutStateAsync(preview.BatchId))!;
        Assert.Equal("failed", failed.Status);
        Assert.Equal(0, failed.PaidCents);
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.Cash));
        Assert.Equal(-1200, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.Equal(2, (await reopened.GetPayoutItemAdjustmentsAsync(preview.BatchId)).Count);
        Assert.Equal(0, provider.SendCalls);
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task TerminalReconciliation_FailsClosedOnPromotionRewriteOrMissingProviderBatch()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = new PayoutBatchPreview(
            "batch:terminal-conflict",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [
                new PayoutItem("contributor:one", 500, Approval("contributor:one")),
                new PayoutItem("contributor:two", 700, Approval("contributor:two")),
            ]);
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        var initial = new PayoutBatchResult(
            preview.BatchId,
            PayoutBatchOutcome.PartiallySucceeded,
            "SUCCESS",
            [
                new PayoutItemResult("contributor:one", 500, true, "SUCCESS"),
                new PayoutItemResult("contributor:two", 700, false, "FAILED"),
            ]);
        Assert.True(await repository.CompletePayoutAsync(preview, initial, now));
        var provider = new ReconcilableFixtureProvider();
        provider.SetLookup(
            preview.BatchId,
            SandboxPayoutLookupState.Succeeded,
            "SUCCESS",
            [
                new PayoutItemResult("contributor:one", 500, true, "SUCCESS"),
                new PayoutItemResult("contributor:two", 700, true, "SUCCESS"),
            ]);
        var coordinator = new DurablePayoutCoordinator(repository, provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ReconcileTerminalAsync(now.AddMinutes(1), now.AddMinutes(1)));
        Assert.Equal(500, (await repository.GetPayoutStateAsync(preview.BatchId))!.PaidCents);
        Assert.Empty(await repository.GetPayoutItemAdjustmentsAsync(preview.BatchId));
        Assert.Equal(0, provider.SendCalls);

        provider.SetLookup(preview.BatchId, SandboxPayoutLookupState.NotFound, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ReconcileTerminalAsync(now.AddMinutes(2), now.AddMinutes(2)));
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task FailedItemRemediation_RetriesOnlyReviewedFailureAndPersistsLineage()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var source = await CompletePartiallyFailedPayoutAsync(
            repository, "batch:remediation-source");
        var replacement = PayoutRecipientApproval.Create(
            "snapshot:replacement-two", "replacement-two@personal.example.com");
        var remediation = new PayoutBatchPreview(
            "batch:remediation-target",
            PayoutExecutionMode.Sandbox,
            source.EligibilityDate,
            [new PayoutItem("contributor:two", 700, replacement)]);
        var review = await repository.ReviewFailedPayoutRemediationAsync(
            source.BatchId, remediation, now.AddMinutes(1));

        var reviewedItem = Assert.Single(review.Items);
        Assert.Equal(1, reviewedItem.SourceOrdinal);
        Assert.Equal("contributor:two", reviewedItem.ContributorPseudonym);
        Assert.Equal("FAILED", reviewedItem.SourceProviderStatus);
        Assert.Equal(replacement.SnapshotId, reviewedItem.ReplacementRecipientSnapshotId);
        Assert.Equal(replacement.DestinationSha256, reviewedItem.ReplacementDestinationSha256);
        Assert.Equal(now.AddMinutes(11), review.ExpiresUtc);

        var provider = new FixtureProvider(true);
        var coordinator = new DurablePayoutCoordinator(repository, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteFailedItemRemediationAsync(
                review, true, false, remediation.EligibilityDate, now.AddMinutes(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteFailedItemRemediationAsync(
                review, false, true, remediation.EligibilityDate, now.AddMinutes(2)));
        Assert.Equal(0, provider.Calls);
        Assert.Null(await repository.GetPayoutStateAsync(remediation.BatchId));

        var result = await coordinator.ExecuteFailedItemRemediationAsync(
            review, true, true, remediation.EligibilityDate, now.AddMinutes(2));

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("partially_succeeded", (await repository.GetPayoutStateAsync(source.BatchId))!.Status);
        Assert.Equal("succeeded", (await repository.GetPayoutStateAsync(remediation.BatchId))!.Status);
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.Equal(-1200, await repository.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await repository.ReconcilesAsync());

        var reopened = await DurableCareRepository.OpenAsync(root);
        var lineage = Assert.Single(await reopened.GetPayoutItemRemediationsAsync(source.BatchId));
        Assert.Equal(source.BatchId, lineage.SourceBatchId);
        Assert.Equal(1, lineage.SourceOrdinal);
        Assert.Equal(remediation.BatchId, lineage.RemediationBatchId);
        Assert.Equal(0, lineage.RemediationOrdinal);
        Assert.Equal(review.ReviewSha256, lineage.ReviewSha256);
        Assert.Equal("succeeded", lineage.RemediationStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DurablePayoutCoordinator(reopened, provider).ExecuteFailedItemRemediationAsync(
                review, true, true, remediation.EligibilityDate, now.AddMinutes(3)));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task FailedItemRemediation_DeniesSuccessTamperExpiryAndDuplicateSource()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var source = await CompletePartiallyFailedPayoutAsync(
            repository, "batch:remediation-denials");
        var successfulItemPreview = new PayoutBatchPreview(
            "batch:cannot-retry-success",
            PayoutExecutionMode.Sandbox,
            source.EligibilityDate,
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReviewFailedPayoutRemediationAsync(
                source.BatchId, successfulItemPreview, now.AddMinutes(1)));

        var remediation = new PayoutBatchPreview(
            "batch:remediation-denial-target",
            PayoutExecutionMode.Sandbox,
            source.EligibilityDate,
            [new PayoutItem("contributor:two", 700, Approval("replacement:two"))]);
        var review = await repository.ReviewFailedPayoutRemediationAsync(
            source.BatchId, remediation, now.AddMinutes(1));
        var tampered = review with { ReviewSha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReserveFailedPayoutRemediationAsync(
                tampered, true, true, remediation.EligibilityDate, now.AddMinutes(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReserveFailedPayoutRemediationAsync(
                review, true, true, remediation.EligibilityDate, now.AddMinutes(12)));
        Assert.Null(await repository.GetPayoutStateAsync(remediation.BatchId));
        Assert.Empty(await repository.GetPayoutItemRemediationsAsync(source.BatchId));

        Assert.True(await repository.ReserveFailedPayoutRemediationAsync(
            review, true, true, remediation.EligibilityDate, now.AddMinutes(2)));
        Assert.False(await repository.ReserveFailedPayoutRemediationAsync(
            review, true, true, remediation.EligibilityDate, now.AddMinutes(2)));
        Assert.Equal("reserved", (await repository.GetPayoutStateAsync(remediation.BatchId))!.Status);
        Assert.Single(await repository.GetPayoutItemRemediationsAsync(source.BatchId));

        var duplicate = remediation with { BatchId = "batch:second-remediation-target" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReviewFailedPayoutRemediationAsync(
                source.BatchId, duplicate, now.AddMinutes(3)));
    }

    [Fact]
    public async Task FailedItemRemediation_ProviderFailureLeavesApprovedReservationForSafeRestart()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var source = Preview("batch:remediation-crash-source");
        await AccrueAsync(repository, source);
        await new DurablePayoutCoordinator(repository, new FixtureProvider(false)).ExecuteAsync(
            source, true, true, source.EligibilityDate, now);
        var remediation = source with
        {
            BatchId = "batch:remediation-crash-target",
            Items =
            [
                new PayoutItem(
                    "contributor:one",
                    500,
                    PayoutRecipientApproval.Create(
                        "snapshot:crash-replacement",
                        "crash-replacement@personal.example.com")),
            ],
        };
        var review = await repository.ReviewFailedPayoutRemediationAsync(
            source.BatchId, remediation, now.AddMinutes(1));
        var throwingProvider = new ThrowingProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DurablePayoutCoordinator(repository, throwingProvider)
                .ExecuteFailedItemRemediationAsync(
                    review, true, true, remediation.EligibilityDate, now.AddMinutes(2)));

        Assert.Equal(1, throwingProvider.Calls);
        Assert.Equal("reserved", (await repository.GetPayoutStateAsync(remediation.BatchId))!.Status);
        Assert.Single(await repository.GetPayoutItemRemediationsAsync(source.BatchId));

        var reopened = await DurableCareRepository.OpenAsync(root);
        var provider = new ReconcilableFixtureProvider();
        var summary = await new DurablePayoutCoordinator(reopened, provider).ReconcilePendingAsync(
            now.AddMinutes(3), now.AddMinutes(3));

        Assert.Equal(new PayoutReconciliationSummary(1, 1, 0, 1), summary);
        Assert.Equal(1, provider.SendCalls);
        Assert.Equal("succeeded", (await reopened.GetPayoutStateAsync(remediation.BatchId))!.Status);
        Assert.Equal(0, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task FailedItemRemediation_MissingLineageBlocksRestartAndCompletion()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var source = await CompletePartiallyFailedPayoutAsync(
            repository, "batch:remediation-lineage-source");
        var remediation = new PayoutBatchPreview(
            "batch:remediation-lineage-target",
            PayoutExecutionMode.Sandbox,
            source.EligibilityDate,
            [new PayoutItem("contributor:two", 700, Approval("lineage:replacement"))]);
        var review = await repository.ReviewFailedPayoutRemediationAsync(
            source.BatchId, remediation, now.AddMinutes(1));
        Assert.True(await repository.ReserveFailedPayoutRemediationAsync(
            review, true, true, remediation.EligibilityDate, now.AddMinutes(2)));

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM payout_item_remediations WHERE remediation_batch_id = $batch;";
            command.Parameters.AddWithValue("$batch", remediation.BatchId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reopened = await DurableCareRepository.OpenAsync(root);
        var provider = new ReconcilableFixtureProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DurablePayoutCoordinator(reopened, provider).ReconcilePendingAsync(
                now.AddMinutes(3), now.AddMinutes(3)));
        Assert.Equal(0, provider.SendCalls);

        var terminal = new PayoutBatchResult(
            remediation.BatchId,
            PayoutBatchOutcome.Succeeded,
            "SUCCESS",
            [new PayoutItemResult("contributor:two", 700, true, "SUCCESS")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.CompletePayoutAsync(remediation, terminal, now.AddMinutes(3)));
        Assert.Equal("reserved", (await reopened.GetPayoutStateAsync(remediation.BatchId))!.Status);
    }

    [Fact]
    public async Task SchemaThreeReservedPayout_IsQuarantinedForRecipientReapproval()
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
                CREATE TABLE payout_batches(
                    batch_id TEXT PRIMARY KEY,
                    preview_sha256 TEXT NOT NULL,
                    preview_json TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('reserved', 'succeeded', 'failed')),
                    provider_status TEXT,
                    provider_batch_id TEXT,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO payout_batches(
                    batch_id, preview_sha256, preview_json, status, provider_batch_id,
                    created_utc, updated_utc)
                VALUES('batch:legacy-reserved', 'legacy-preview-digest', '{}', 'reserved',
                    'PB-LEGACY', $created, $updated);
                PRAGMA user_version = 3;
                """;
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = await DurableCareRepository.OpenAsync(root);
        var state = (await repository.GetPayoutStateAsync("batch:legacy-reserved"))!;
        Assert.Equal("review_required", state.Status);
        Assert.Equal("RECIPIENT_REAPPROVAL_REQUIRED", state.ProviderStatus);
        Assert.Equal("PB-LEGACY", await repository.GetProviderBatchIdAsync("batch:legacy-reserved"));
        Assert.Empty(await repository.GetPendingPayoutsAsync(now.AddDays(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.StoreProviderBatchIdAsync("batch:legacy-reserved", "PB-LEGACY"));

        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        var version = reopened.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(7L, (long)(await version.ExecuteScalarAsync())!);
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
        [new PayoutItem("contributor:one", 500, Approval("contributor:one"))]);

    private static PayoutRecipientApproval Approval(string contributor) =>
        PayoutRecipientApproval.Create(
            $"snapshot:{contributor.Replace(':', '-')}",
            $"{contributor.Replace(':', '-')}@personal.example.com");

    private async Task AccrueAsync(DurableCareRepository repository, PayoutBatchPreview preview) =>
        await repository.PostAsync(new LedgerJournalEntry(
            $"accrual:{preview.BatchId}",
            now,
            "fixture_contributor_accrual",
            [
                new LedgerPosting(LedgerAccount.ContributorExpense, preview.TotalCents, 0),
                new LedgerPosting(LedgerAccount.ContributorPayable, 0, preview.TotalCents),
            ]));

    private async Task<PayoutBatchPreview> CompletePartiallyFailedPayoutAsync(
        DurableCareRepository repository,
        string batchId)
    {
        var preview = new PayoutBatchPreview(
            batchId,
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [
                new PayoutItem("contributor:one", 500, Approval("contributor:one")),
                new PayoutItem("contributor:two", 700, Approval("contributor:two")),
            ]);
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        Assert.True(await repository.CompletePayoutAsync(
            preview,
            new PayoutBatchResult(
                preview.BatchId,
                PayoutBatchOutcome.PartiallySucceeded,
                "SUCCESS",
                [
                    new PayoutItemResult("contributor:one", 500, true, "SUCCESS"),
                    new PayoutItemResult("contributor:two", 700, false, "FAILED"),
                ]),
            now));
        return preview;
    }

    private sealed class FixtureProvider(bool succeeds) : IPayoutSandboxProvider
    {
        public int Calls { get; private set; }

        public Task<PayoutBatchResult> SendSandboxAsync(PayoutBatchPreview preview, CancellationToken cancellationToken)
        {
            Calls++;
            var status = succeeds ? "SANDBOX_SUCCESS" : "SANDBOX_FAILED";
            return Task.FromResult(new PayoutBatchResult(
                preview.BatchId,
                succeeds ? PayoutBatchOutcome.Succeeded : PayoutBatchOutcome.Failed,
                status,
                preview.Items.Select(item => new PayoutItemResult(
                    item.ContributorPseudonym,
                    item.AmountCents,
                    succeeds,
                    status)).ToArray()));
        }
    }

    private sealed class ReconcilableFixtureProvider : IReconcilablePayoutSandboxProvider
    {
        private readonly Dictionary<string, SandboxPayoutLookupResult> lookups = new(StringComparer.Ordinal);

        public int SendCalls { get; private set; }

        public void SetLookup(
            string batchId,
            SandboxPayoutLookupState state,
            string? providerStatus,
            IReadOnlyList<PayoutItemResult>? itemResults = null) =>
            lookups[batchId] = new SandboxPayoutLookupResult(
                batchId, state, providerStatus, itemResults);

        public Task<SandboxPayoutLookupResult> LookupSandboxAsync(
            PayoutBatchPreview preview,
            CancellationToken cancellationToken)
        {
            var lookup = lookups.GetValueOrDefault(preview.BatchId) ??
                new SandboxPayoutLookupResult(
                    preview.BatchId, SandboxPayoutLookupState.NotFound, null);
            if (lookup.State == SandboxPayoutLookupState.Succeeded &&
                lookup.ItemResults is null)
            {
                lookup = lookup with
                {
                    ItemResults = preview.Items.Select(item => new PayoutItemResult(
                        item.ContributorPseudonym,
                        item.AmountCents,
                        true,
                        lookup.ProviderStatus!)).ToArray(),
                };
            }

            return Task.FromResult(lookup);
        }

        public Task<PayoutBatchResult> SendSandboxAsync(
            PayoutBatchPreview preview,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            var results = preview.Items.Select(item => new PayoutItemResult(
                item.ContributorPseudonym,
                item.AmountCents,
                true,
                "SANDBOX_SUCCESS")).ToArray();
            lookups[preview.BatchId] = new SandboxPayoutLookupResult(
                preview.BatchId,
                SandboxPayoutLookupState.Succeeded,
                "SANDBOX_SUCCESS",
                results);
            return Task.FromResult(new PayoutBatchResult(
                preview.BatchId,
                PayoutBatchOutcome.Succeeded,
                "SANDBOX_SUCCESS",
                results));
        }
    }

    private sealed class PendingFixtureProvider : IPayoutSandboxProvider
    {
        public Task<PayoutBatchResult> SendSandboxAsync(
            PayoutBatchPreview preview,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PayoutBatchResult(
                preview.BatchId,
                PayoutBatchOutcome.Pending,
                "PENDING"));
    }

    private sealed class ThrowingProvider : IPayoutSandboxProvider
    {
        public int Calls { get; private set; }

        public Task<PayoutBatchResult> SendSandboxAsync(
            PayoutBatchPreview preview,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Synthetic provider failure.");
        }
    }
}
