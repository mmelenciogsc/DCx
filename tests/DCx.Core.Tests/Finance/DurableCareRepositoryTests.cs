using System.Security.Cryptography;
using System.Text.Json;
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
        Assert.Equal(15L, (long)(await versionCommand.ExecuteScalarAsync())!);
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
            command.CommandText = "PRAGMA user_version = 16;";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DurableCareRepository.OpenAsync(root));
        Assert.Contains("newer than this application", exception.Message, StringComparison.Ordinal);

        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        var versionCommand = reopened.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(16L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaTen_BackfillsOpaqueCaseForExistingBlockedEvent()
    {
        _ = await DurableCareRepository.OpenAsync(root);
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
                INSERT INTO webhook_event_quarantine(
                    event_id, transmission_id, event_type, provider_object_id,
                    subscription_id, plan_id, related_sale_id, amount_cents,
                    currency, occurred_utc, payload_sha256, expected_plan_id,
                    verified_event_sha256, reason_code, state, attempt_count,
                    first_received_utc, last_attempt_utc, next_attempt_utc,
                    completed_utc)
                VALUES(
                    'WH-LEGACY-BLOCKED', 'transmission:legacy-blocked',
                    'PAYMENT.SALE.REFUNDED', 'REFUND-LEGACY-BLOCKED',
                    NULL, NULL, 'SALE-LEGACY-BLOCKED', 100, 'USD', $occurred,
                    $payload, 'P-DCX300', $verified,
                    'paypal_sandbox_quarantine_reprocess_blocked', 'blocked', 1,
                    $first, $attempted, NULL, NULL);
                DROP TRIGGER care_blocked_case_dispositions_no_delete;
                DROP TRIGGER care_blocked_case_dispositions_no_update;
                DROP TRIGGER care_blocked_cases_no_delete;
                DROP TRIGGER care_blocked_cases_no_update;
                DROP TABLE care_blocked_case_dispositions;
                DROP TABLE care_blocked_cases;
                PRAGMA user_version = 10;
                """;
            command.Parameters.AddWithValue("$occurred", now.AddMinutes(-2).ToString("O"));
            command.Parameters.AddWithValue("$first", now.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$attempted", now.ToString("O"));
            command.Parameters.AddWithValue("$payload", new string('a', 64));
            command.Parameters.AddWithValue("$verified", new string('b', 64));
            await command.ExecuteNonQueryAsync();
        }

        var reopened = await DurableCareRepository.OpenAsync(root);
        var blockedCase = Assert.Single(await reopened.ListBlockedCasesAsync());
        Assert.Equal("refund", blockedCase.EventKind);
        Assert.Equal("policy_rejected", blockedCase.ReasonCode);
        Assert.Equal(CareBlockedCaseReviewPolicy.Unreviewed, blockedCase.DispositionCode);
        Assert.DoesNotContain("LEGACY", JsonSerializer.Serialize(blockedCase),
            StringComparison.Ordinal);

        await using var verifiedConnection = new SqliteConnection(connectionString);
        await verifiedConnection.OpenAsync();
        var version = verifiedConnection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaSix_AddsRemediationAndWebhookQuarantineWithoutReplacingPriorState()
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
            command.CommandText = """
                DROP TABLE payout_item_remediations;
                DROP TABLE webhook_event_quarantine;
                PRAGMA user_version = 6;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var reopened = await DurableCareRepository.OpenAsync(root);
        Assert.Equal("failed", (await reopened.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Empty(await reopened.GetPayoutItemRemediationsAsync(preview.BatchId));

        await using var verified = new SqliteConnection(connectionString);
        await verified.OpenAsync();
        var version = verified.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
        var table = verified.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'payout_item_remediations';";
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'webhook_event_quarantine';";
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
        await RegisterAsync(
            repository,
            new CarePeriodInput(2026, 1, 10_000, 0, 0, 0, 0, MinimumPayoutCents: 100),
            [new ContributorUsageWeight("contributor:one", 1m)],
            now);
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
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
            "care:2026-01");
        await AccrueAsync(repository, preview);
        var result = await new DurablePayoutCoordinator(repository, new FixtureProvider(false)).ExecuteAsync(
            preview, true, true, new DateOnly(2026, 4, 1), now);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
    }

    [Fact]
    public async Task CarePeriodRegistration_IsAtomicIdempotentAndConflictSafeAcrossReopen()
    {
        var input = new CarePeriodInput(
            2026, 1, 3_000, 0, 0, 0, 0,
            ContributorPoolBasisPoints: 4000,
            MinimumPayoutCents: 0);
        ContributorUsageWeight[] usage =
        [
            new("contributor:two", 2m),
            new("contributor:one", 1m),
        ];
        var repository = await DurableCareRepository.OpenAsync(root);

        Assert.Throws<InvalidOperationException>(() =>
            DurableCareRepository.ReviewCarePeriodClose(
                input,
                [new ContributorUsageWeight(new string('x', 129), 1m)],
                now));

        var review = DurableCareRepository.ReviewCarePeriodClose(input, usage, now);
        Assert.Equal(now.AddMinutes(10), review.ExpiresUtc);
        Assert.Equal(64, review.ReviewSha256.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(review, false, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(
                review with { ReviewSha256 = new string('0', 64) },
                true,
                now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(
                review with
                {
                    Usage =
                    [
                        new ContributorUsageWeight("contributor:one", 2m),
                        new ContributorUsageWeight("contributor:two", 2m),
                    ],
                },
                true,
                now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(
                review with
                {
                    Result = review.Result with
                    {
                        AggregateContributorPoolCents =
                            review.Result.AggregateContributorPoolCents + 1,
                    },
                },
                true,
                now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(review, true, now.AddSeconds(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.RegisterReviewedCarePeriodAsync(review, true, now.AddMinutes(11)));
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.ContributorPayable));

        var registered = await repository.RegisterReviewedCarePeriodAsync(review, true, now);

        Assert.Equal(1_200, registered.AggregateContributorPoolCents);
        Assert.Collection(
            registered.Allocations,
            item =>
            {
                Assert.Equal("contributor:one", item.ContributorPseudonym);
                Assert.Equal(400, item.AmountCents);
            },
            item =>
            {
                Assert.Equal("contributor:two", item.ContributorPseudonym);
                Assert.Equal(800, item.AmountCents);
            });
        Assert.Equal(-1_200, await repository.BalanceAsync(LedgerAccount.ContributorPayable));

        var reopened = await DurableCareRepository.OpenAsync(root);
        var replay = await reopened.RegisterReviewedCarePeriodAsync(
            review, true, now.AddMinutes(1));
        Assert.Equal(registered.AggregateContributorPoolCents, replay.AggregateContributorPoolCents);
        Assert.Equal(registered.Net60EligibilityDate, replay.Net60EligibilityDate);
        Assert.Equal(registered.Allocations.ToArray(), replay.Allocations.ToArray());
        Assert.Equal(-1_200, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.RegisterReviewedCarePeriodAsync(
                DurableCareRepository.ReviewCarePeriodClose(
                    input,
                    usage,
                    now.AddMinutes(2)),
                true,
                now.AddMinutes(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.RegisterReviewedCarePeriodAsync(
                DurableCareRepository.ReviewCarePeriodClose(
                    input with { SubscriptionRevenueCents = 3_001 },
                    usage,
                    now.AddMinutes(2)),
                true,
                now.AddMinutes(2)));
        Assert.Equal(-1_200, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task ContributorDashboard_DerivesWithheldEligiblePendingPartialAndPaidState()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var input = new CarePeriodInput(
            2026, 1, 1_250, 0, 0, 0, 0,
            ContributorPoolBasisPoints: 4000,
            MinimumPayoutCents: 0);
        var result = await RegisterAsync(
            repository,
            input,
            [new ContributorUsageWeight("contributor:one", 1m)],
            now);

        var unknown = await repository.GetContributorDashboardAsync(
            "contributor:unknown", now);
        CareContributorDashboardPolicy.RequireSnapshot(unknown);
        Assert.Equal(CareContributorDashboardPolicy.Available, unknown.StatusCode);
        Assert.Empty(unknown.Periods);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetContributorDashboardAsync(
                "contributor:one", now.ToOffset(TimeSpan.FromHours(8))));

        var withheld = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:one", now)).Periods);
        Assert.Equal(CareContributorDashboardPolicy.Withheld, withheld.StatusCode);
        Assert.Equal(500, withheld.AllocationCents);
        Assert.Equal(0, withheld.PaidCents);
        Assert.Equal(500, withheld.OutstandingCents);

        var eligibleTime = new DateTimeOffset(
            result.Net60EligibilityDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var eligible = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:one", eligibleTime)).Periods);
        Assert.Equal(CareContributorDashboardPolicy.Eligible, eligible.StatusCode);

        var first = DashboardPreview(
            "batch:dashboard-first", result, 200, "snapshot:dashboard-first");
        Assert.True(await repository.ReservePayoutAsync(
            first, true, true, first.EligibilityDate, eligibleTime));
        var pending = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:one", eligibleTime)).Periods);
        Assert.Equal(CareContributorDashboardPolicy.Pending, pending.StatusCode);
        Assert.Equal(0, pending.PaidCents);
        Assert.Equal(500, pending.OutstandingCents);

        Assert.True(await repository.CompletePayoutAsync(
            first,
            new PayoutBatchResult(
                first.BatchId,
                PayoutBatchOutcome.Succeeded,
                "SUCCESS",
                [new PayoutItemResult("contributor:one", 200, true, "SUCCESS")]),
            eligibleTime.AddMinutes(1)));
        var partial = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:one", eligibleTime.AddMinutes(1))).Periods);
        Assert.Equal(CareContributorDashboardPolicy.PartiallyPaid, partial.StatusCode);
        Assert.Equal(200, partial.PaidCents);
        Assert.Equal(300, partial.OutstandingCents);

        var second = DashboardPreview(
            "batch:dashboard-second", result, 300, "snapshot:dashboard-second");
        Assert.True(await repository.ReservePayoutAsync(
            second, true, true, second.EligibilityDate, eligibleTime.AddMinutes(2)));
        var partiallyPaidPending = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:one", eligibleTime.AddMinutes(2))).Periods);
        Assert.Equal(CareContributorDashboardPolicy.Pending, partiallyPaidPending.StatusCode);
        Assert.Equal(200, partiallyPaidPending.PaidCents);
        Assert.Equal(300, partiallyPaidPending.OutstandingCents);

        Assert.True(await repository.CompletePayoutAsync(
            second,
            new PayoutBatchResult(
                second.BatchId,
                PayoutBatchOutcome.Succeeded,
                "SUCCESS",
                [new PayoutItemResult("contributor:one", 300, true, "SUCCESS")]),
            eligibleTime.AddMinutes(3)));
        var reopened = await DurableCareRepository.OpenAsync(root);
        var source = new DurableCareContributorDashboardSource(
            reopened,
            "contributor:one",
            () => eligibleTime.AddMinutes(4));
        var paid = Assert.Single((await source.LoadAsync()).Periods);
        Assert.Equal(CareContributorDashboardPolicy.Paid, paid.StatusCode);
        Assert.Equal(500, paid.PaidCents);
        Assert.Equal(0, paid.OutstandingCents);

        Assert.True(await reopened.ReconcileTerminalPayoutAsync(
            second,
            new PayoutBatchResult(
                second.BatchId,
                PayoutBatchOutcome.Failed,
                "SUCCESS",
                [new PayoutItemResult("contributor:one", 300, false, "RETURNED")]),
            eligibleTime.AddMinutes(5)));
        var returned = Assert.Single((await reopened.GetContributorDashboardAsync(
            "contributor:one", eligibleTime.AddMinutes(6))).Periods);
        Assert.Equal(CareContributorDashboardPolicy.PartiallyPaid, returned.StatusCode);
        Assert.Equal(200, returned.PaidCents);
        Assert.Equal(300, returned.OutstandingCents);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE payout_batches SET paid_cents = 199 WHERE batch_id = $batch;
                """;
            command.Parameters.AddWithValue("$batch", first.BatchId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadAsync());
    }

    [Fact]
    public async Task ContributorDashboard_PreservesThresholdHoldAndDeniesAllocationTamper()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var result = await RegisterAsync(
            repository,
            new CarePeriodInput(
                2026, 2, 1_250, 0, 0, 0, 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 600),
            [new ContributorUsageWeight("contributor:held", 1m)],
            now);
        var observed = new DateTimeOffset(
            result.Net60EligibilityDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        var held = Assert.Single((await repository.GetContributorDashboardAsync(
            "contributor:held", observed)).Periods);
        Assert.Equal(CareContributorDashboardPolicy.HeldBelowThreshold, held.StatusCode);
        Assert.Equal(500, held.OutstandingCents);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_contributor_allocations
                SET allocation_cents = 499
                WHERE period_id = $period AND contributor_pseudonym = 'contributor:held';
                """;
            command.Parameters.AddWithValue("$period", PayoutBatchPreviewFactory.CarePeriodId(result));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetContributorDashboardAsync("contributor:held", observed));
    }

    [Fact]
    public async Task PayoutReservation_RejectsPeriodContributorAmountSubstitutionAndHeldAllocation()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var payable = await RegisterAsync(
            repository,
            new CarePeriodInput(
                2026, 1, 3_000, 0, 0, 0, 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 0),
            [
                new ContributorUsageWeight("contributor:one", 5m),
                new ContributorUsageWeight("contributor:two", 7m),
            ],
            now);
        var eligible = payable.Net60EligibilityDate;
        var period = PayoutBatchPreviewFactory.CarePeriodId(payable);

        var wrongContributor = new PayoutBatchPreview(
            "batch:wrong-contributor",
            PayoutExecutionMode.Sandbox,
            eligible,
            [new PayoutItem("contributor:three", 500, Approval("contributor:three"))],
            period);
        var wrongAmount = new PayoutBatchPreview(
            "batch:wrong-amount",
            PayoutExecutionMode.Sandbox,
            eligible,
            [new PayoutItem("contributor:one", 600, Approval("contributor:one"))],
            period);
        var wrongPeriod = new PayoutBatchPreview(
            "batch:wrong-period",
            PayoutExecutionMode.Sandbox,
            eligible,
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
            "care:2026-02");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReservePayoutAsync(
            wrongContributor, true, true, eligible, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReservePayoutAsync(
            wrongAmount, true, true, eligible, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReservePayoutAsync(
            wrongPeriod, true, true, eligible, now));
        Assert.Null(await repository.GetPayoutStateAsync(wrongContributor.BatchId));
        Assert.Null(await repository.GetPayoutStateAsync(wrongAmount.BatchId));
        Assert.Null(await repository.GetPayoutStateAsync(wrongPeriod.BatchId));

        var held = await RegisterAsync(
            repository,
            new CarePeriodInput(
                2026, 2, 1_250, 0, 0, 0, 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 600),
            [new ContributorUsageWeight("contributor:held", 1m)],
            now);
        var heldPreview = new PayoutBatchPreview(
            "batch:held-allocation",
            PayoutExecutionMode.Sandbox,
            held.Net60EligibilityDate,
            [new PayoutItem("contributor:held", 500, Approval("contributor:held"))],
            PayoutBatchPreviewFactory.CarePeriodId(held));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReservePayoutAsync(
            heldPreview, true, true, heldPreview.EligibilityDate, now));
        Assert.Null(await repository.GetPayoutStateAsync(heldPreview.BatchId));

        var exact = new PayoutBatchPreview(
            "batch:exact-allocation",
            PayoutExecutionMode.Sandbox,
            eligible,
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
            period);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_contributor_allocations
                SET allocation_cents = 600
                WHERE period_id = $period AND contributor_pseudonym = 'contributor:one';
                """;
            command.Parameters.AddWithValue("$period", period);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReservePayoutAsync(
            exact, true, true, exact.EligibilityDate, now));
        Assert.Null(await repository.GetPayoutStateAsync(exact.BatchId));
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_contributor_allocations
                SET allocation_cents = 500
                WHERE period_id = $period AND contributor_pseudonym = 'contributor:one';
                """;
            command.Parameters.AddWithValue("$period", period);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.True(await repository.ReservePayoutAsync(
            exact, true, true, exact.EligibilityDate, now));
        Assert.Single(await repository.GetPendingPayoutsAsync(now.AddMinutes(1)));
    }

    [Fact]
    public async Task PayoutReservation_RequiresAndAtomicallyReservesOutstandingLiabilityAcrossRepositories()
    {
        var firstRepository = await DurableCareRepository.OpenAsync(root);
        var secondRepository = await DurableCareRepository.OpenAsync(root);
        var first = Preview("batch:liability-first");
        var second = Preview("batch:liability-second");

        var missingLiability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstRepository.ReservePayoutAsync(
                first, true, true, first.EligibilityDate, now));
        Assert.Contains("registered CARE period", missingLiability.Message,
            StringComparison.Ordinal);
        Assert.Null(await firstRepository.GetPayoutStateAsync(first.BatchId));

        await RegisterAsync(
            firstRepository,
            new CarePeriodInput(
                2026, 1, 2_500, 0, 0, 0, 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 0),
            [
                new ContributorUsageWeight("contributor:one", 1m),
                new ContributorUsageWeight("contributor:two", 1m),
            ],
            now);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> AttemptAsync(
            DurableCareRepository repository,
            PayoutBatchPreview preview)
        {
            await start.Task;
            try
            {
                return await repository.ReservePayoutAsync(
                    preview, true, true, preview.EligibilityDate, now)
                    ? "reserved"
                    : "replay";
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("unreserved CARE allocation", StringComparison.Ordinal))
            {
                return "allocation_denied";
            }
        }

        var attempts = new[]
        {
            AttemptAsync(firstRepository, first),
            AttemptAsync(secondRepository, second),
        };
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result == "reserved");
        Assert.Single(results, result => result == "allocation_denied");
        var pending = await firstRepository.GetPendingPayoutsAsync(now.AddMinutes(1));
        var reservation = Assert.Single(pending);
        Assert.Equal(500, reservation.Preview.TotalCents);
        Assert.Equal(-1_000, await firstRepository.BalanceAsync(LedgerAccount.ContributorPayable));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_total_cents FROM payout_batches WHERE status = 'reserved';";
        Assert.Equal(500L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PayoutCompletion_RevalidatesLiabilityBeforeFinancialMutation()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:liability-recheck");
        await AccrueAsync(repository, preview);
        Assert.True(await repository.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PostAsync(new LedgerJournalEntry(
            "fixture:liability-release",
            now.AddSeconds(1),
            "fixture_liability_release",
            [
                new LedgerPosting(LedgerAccount.ContributorPayable, 500, 0),
                new LedgerPosting(LedgerAccount.SubscriptionRevenue, 0, 500),
            ])));
        Assert.Equal(-500, await repository.BalanceAsync(LedgerAccount.ContributorPayable));

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ledger_entries(event_id, occurred_utc, description_code, canonical_sha256)
                VALUES('fixture:hostile-liability-edit', $occurred, 'fixture_hostile_edit', $sha);
                INSERT INTO ledger_postings(event_id, ordinal, account, debit_cents, credit_cents)
                VALUES('fixture:hostile-liability-edit', 0, $payable, 500, 0);
                INSERT INTO ledger_postings(event_id, ordinal, account, debit_cents, credit_cents)
                VALUES('fixture:hostile-liability-edit', 1, $revenue, 0, 500);
                """;
            command.Parameters.AddWithValue("$occurred", now.AddSeconds(1).ToString("O"));
            command.Parameters.AddWithValue("$sha", new string('a', 64));
            command.Parameters.AddWithValue("$payable", (int)LedgerAccount.ContributorPayable);
            command.Parameters.AddWithValue("$revenue", (int)LedgerAccount.SubscriptionRevenue);
            await command.ExecuteNonQueryAsync();
        }
        var result = new PayoutBatchResult(
            preview.BatchId,
            PayoutBatchOutcome.Succeeded,
            "SUCCESS",
            [new PayoutItemResult("contributor:one", 500, true, "SUCCESS")]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CompletePayoutAsync(preview, result, now.AddSeconds(2)));

        Assert.Contains("outstanding contributor payable", exception.Message,
            StringComparison.Ordinal);
        Assert.Equal("reserved", (await repository.GetPayoutStateAsync(preview.BatchId))!.Status);
        Assert.Empty(await repository.GetPayoutItemStatesAsync(preview.BatchId));
        Assert.Equal(0, await repository.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await repository.ReconcilesAsync());
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
        await AccrueAsync(repository, preview);
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
        await AccrueAsync(repository, preview);
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
        await AccrueAsync(repository, preview);
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

        var second = Preview("batch:provider-reference-two", month: 2);
        await AccrueAsync(reopened, second);
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
            ],
            "care:2026-01");
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
            ],
            "care:2026-01");
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
        var returnedHold = Assert.Single(await reopened.ListFailedPayoutReviewsAsync());
        Assert.Equal(500, returnedHold.AmountCents);
        Assert.Equal(CareFailedPayoutReviewPolicy.Returned, returnedHold.FailureCode);
        Assert.Equal(now.AddMinutes(1), returnedHold.FailedAtUtc);
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
            ],
            "care:2026-01");
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
            [new PayoutItem("contributor:two", 700, replacement)],
            source.CarePeriodId);
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
        Assert.Empty(await repository.ListFailedPayoutReviewsAsync());

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
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
            source.CarePeriodId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReviewFailedPayoutRemediationAsync(
                source.BatchId, successfulItemPreview, now.AddMinutes(1)));

        var remediation = new PayoutBatchPreview(
            "batch:remediation-denial-target",
            PayoutExecutionMode.Sandbox,
            source.EligibilityDate,
            [new PayoutItem("contributor:two", 700, Approval("replacement:two"))],
            source.CarePeriodId);
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
            [new PayoutItem("contributor:two", 700, Approval("lineage:replacement"))],
            source.CarePeriodId);
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
    public async Task FailedPayoutHoldReview_ProjectsMinimizedStateAndPersistsZeroAuthorityDisposition()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var payout = await CompletePartiallyFailedPayoutAsync(
            repository, "batch:hold-review-source");

        var review = Assert.Single(await repository.ListFailedPayoutReviewsAsync());
        Assert.Equal(payout.CarePeriodId, review.CarePeriodId);
        Assert.Equal(700, review.AmountCents);
        Assert.Equal(CareFailedPayoutReviewPolicy.ProviderRejected, review.FailureCode);
        Assert.Equal(CareFailedPayoutReviewPolicy.FailedItemHold, review.HoldCode);
        Assert.Equal(CareFailedPayoutReviewPolicy.NoticeNotReady, review.NoticeCode);
        Assert.Equal(CareFailedPayoutReviewPolicy.Unreviewed, review.DispositionCode);
        Assert.Equal(32, review.ReviewId.Length);
        Assert.Equal(64, review.ReviewSha256.Length);
        var json = JsonSerializer.Serialize(review);
        Assert.DoesNotContain("batch:hold-review-source", json, StringComparison.Ordinal);
        Assert.DoesNotContain("contributor:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("personal.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot:", json, StringComparison.Ordinal);

        var source = new DurableCareFailedPayoutReviewSource(
            repository,
            new string('a', 64),
            () => now.AddMinutes(1));
        var request = new CareFailedPayoutDispositionRequest(
            review.ReviewId,
            review.ReviewSha256,
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            ExplicitlyApproved: true);
        var result = await source.RecordFailedPayoutDispositionAsync(request);
        CareFailedPayoutReviewPolicy.RequireResult(result, request);
        Assert.Equal(0, result.ProviderCalls);
        Assert.Equal(0, result.FinancialMutations);
        Assert.Equal(0, result.PayoutAttempts);
        Assert.Equal(0, result.NotificationTransports);

        var updated = Assert.Single(await source.ListFailedPayoutReviewsAsync());
        Assert.Equal(
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            updated.DispositionCode);
        Assert.Equal(
            CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
            updated.NoticeCode);
        Assert.NotEqual(review.ReviewSha256, updated.ReviewSha256);
        var audit = Assert.Single(await repository.GetFailedPayoutDispositionAuditAsync());
        Assert.Equal(new string('a', 64), audit.OperatorPseudonymSha256);
        Assert.Equal(request.ReviewedSha256, audit.ReviewedSha256);

        var reopened = await DurableCareRepository.OpenAsync(root);
        var persisted = Assert.Single(await reopened.ListFailedPayoutReviewsAsync());
        Assert.Equal(updated, persisted);
        Assert.Equal(-700, await reopened.BalanceAsync(LedgerAccount.ContributorPayable));
        Assert.Equal(-500, await reopened.BalanceAsync(LedgerAccount.Cash));
        Assert.True(await reopened.ReconcilesAsync());
    }

    [Fact]
    public async Task FailedPayoutHoldReview_SerializesReplayAndRejectsConflictAndTamper()
    {
        var firstRepository = await DurableCareRepository.OpenAsync(root);
        _ = await CompletePartiallyFailedPayoutAsync(
            firstRepository, "batch:hold-review-concurrency");
        var review = Assert.Single(await firstRepository.ListFailedPayoutReviewsAsync());
        var secondRepository = await DurableCareRepository.OpenAsync(root);
        var request = new CareFailedPayoutDispositionRequest(
            review.ReviewId,
            review.ReviewSha256,
            CareFailedPayoutReviewPolicy.RetainHold,
            ExplicitlyApproved: true);
        var firstSource = new DurableCareFailedPayoutReviewSource(
            firstRepository, new string('b', 64), () => now.AddMinutes(1));
        var secondSource = new DurableCareFailedPayoutReviewSource(
            secondRepository, new string('b', 64), () => now.AddMinutes(1));

        var results = await Task.WhenAll(
            firstSource.RecordFailedPayoutDispositionAsync(request),
            secondSource.RecordFailedPayoutDispositionAsync(request));
        Assert.Equal(results[0], results[1]);
        Assert.Single(await firstRepository.GetFailedPayoutDispositionAuditAsync());

        var updated = Assert.Single(await firstRepository.ListFailedPayoutReviewsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstSource.RecordFailedPayoutDispositionAsync(request with
            {
                ReviewedSha256 = updated.ReviewSha256,
                DispositionCode = CareFailedPayoutReviewPolicy.CancelRetryIntent,
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstSource.RecordFailedPayoutDispositionAsync(request with
            {
                ReviewedSha256 = new string('0', 64),
            }));

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
            command.CommandText =
                "UPDATE care_failed_payout_dispositions SET disposition_code = 'cancel_retry_intent';";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            command.CommandText =
                "DELETE FROM care_failed_payout_holds WHERE review_id = $review;";
            command.Parameters.AddWithValue("$review", review.ReviewId);
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            command.Parameters.Clear();
            command.CommandText = """
                DROP TRIGGER care_failed_payout_dispositions_no_update;
                UPDATE care_failed_payout_dispositions
                SET record_sha256 = $tampered;
                """;
            command.Parameters.AddWithValue("$tampered", new string('f', 64));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstRepository.GetFailedPayoutDispositionAuditAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstRepository.ListFailedPayoutReviewsAsync());
    }

    [Fact]
    public async Task SchemaFourteen_BackfillsOpaqueFailedPayoutHoldReferences()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        _ = await CompletePartiallyFailedPayoutAsync(
            repository, "batch:hold-review-migration");
        var original = Assert.Single(await repository.ListFailedPayoutReviewsAsync());
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
                DROP TRIGGER care_failed_payout_dispositions_no_delete;
                DROP TRIGGER care_failed_payout_dispositions_no_update;
                DROP TRIGGER care_failed_payout_holds_no_delete;
                DROP TRIGGER care_failed_payout_holds_no_update;
                DROP TABLE care_failed_payout_dispositions;
                DROP TABLE care_failed_payout_holds;
                PRAGMA user_version = 14;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrated = await DurableCareRepository.OpenAsync(root);
        var review = Assert.Single(await migrated.ListFailedPayoutReviewsAsync());
        Assert.NotEqual(original.ReviewId, review.ReviewId);
        Assert.Equal(32, review.ReviewId.Length);
        Assert.Equal(700, review.AmountCents);
        Assert.Equal(CareFailedPayoutReviewPolicy.ProviderRejected, review.FailureCode);
        await using var reopened = new SqliteConnection(connectionString);
        await reopened.OpenAsync();
        var version = reopened.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaTwelve_UnboundTerminalPayoutRemainsReadableButNewReservationsRequireAllocation()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var preview = Preview("batch:schema-twelve-terminal");
        await AccrueAsync(repository, preview);
        var result = await new DurablePayoutCoordinator(repository, new FixtureProvider(true))
            .ExecuteAsync(preview, true, true, preview.EligibilityDate, now);
        Assert.True(result.Succeeded);

        var legacyPreview = preview with { CarePeriodId = null };
        var legacyPayload = new
        {
            legacyPreview.BatchId,
            legacyPreview.Mode,
            legacyPreview.EligibilityDate,
            legacyPreview.Items,
            legacyPreview.TotalCents,
        };
        var legacyJson = JsonSerializer.Serialize(legacyPayload);
        var legacySha = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyPayload)));
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM payout_allocation_bindings;
                DELETE FROM care_contributor_allocations;
                DELETE FROM care_periods;
                UPDATE payout_batches
                SET preview_json = $json,
                    preview_sha256 = $sha,
                    care_period_id = NULL
                WHERE batch_id = $batch;
                PRAGMA user_version = 12;
                """;
            command.Parameters.AddWithValue("$json", legacyJson);
            command.Parameters.AddWithValue("$sha", legacySha);
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            await command.ExecuteNonQueryAsync();
        }

        var reopened = await DurableCareRepository.OpenAsync(root);
        var terminal = Assert.Single(await reopened.GetTerminalPayoutsAsync(now.AddMinutes(1)));
        Assert.Null(terminal.Preview.CarePeriodId);
        Assert.False(await reopened.ReconcileTerminalPayoutAsync(
            terminal.Preview, result, now.AddMinutes(1)));
        var unregistered = Preview("batch:v13-new-reservation", month: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReservePayoutAsync(
            unregistered, true, true, unregistered.EligibilityDate, now.AddMinutes(2)));

        await using var verified = new SqliteConnection(connectionString);
        await verified.OpenAsync();
        var version = verified.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SchemaThirteen_UnreviewedCarePeriodRequiresFreshExactCloseApproval()
    {
        var input = new CarePeriodInput(
            2026, 1, 1_250, 0, 0, 0, 0,
            ContributorPoolBasisPoints: 4000,
            MinimumPayoutCents: 0);
        ContributorUsageWeight[] usage =
        [
            new("contributor:one", 1m),
        ];
        var repository = await DurableCareRepository.OpenAsync(root);
        var result = await RegisterAsync(repository, input, usage, now);
        var preview = new PayoutBatchPreview(
            "batch:schema-thirteen-close-review",
            PayoutExecutionMode.Sandbox,
            result.Net60EligibilityDate,
            [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
            PayoutBatchPreviewFactory.CarePeriodId(result));
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_periods
                SET close_review_sha256 = NULL,
                    close_approval_sha256 = NULL,
                    reviewed_utc = NULL,
                    review_expires_utc = NULL,
                    approved_utc = NULL;
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            command.CommandText = "PRAGMA user_version = 13;";
            await command.ExecuteNonQueryAsync();
        }

        var migrated = await DurableCareRepository.OpenAsync(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migrated.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now.AddMinutes(1)));
        Assert.Null(await migrated.GetPayoutStateAsync(preview.BatchId));

        var competingRepository = await DurableCareRepository.OpenAsync(root);
        var firstReview = DurableCareRepository.ReviewCarePeriodClose(
            input, usage, now.AddMinutes(2));
        var secondReview = DurableCareRepository.ReviewCarePeriodClose(
            input, usage, now.AddMinutes(3));
        async Task<(bool Accepted, CarePeriodCloseReview Review, CarePeriodResult? Result)> AttemptAsync(
            DurableCareRepository candidate,
            CarePeriodCloseReview review)
        {
            try
            {
                return (true, review, await candidate.RegisterReviewedCarePeriodAsync(
                    review, true, review.ReviewedUtc));
            }
            catch (InvalidOperationException)
            {
                return (false, review, null);
            }
        }

        var attempts = await Task.WhenAll(
            AttemptAsync(migrated, firstReview),
            AttemptAsync(competingRepository, secondReview));
        var accepted = Assert.Single(attempts, attempt => attempt.Accepted);
        Assert.Single(attempts, attempt => !attempt.Accepted);
        var freshReview = accepted.Review;
        var promoted = accepted.Result!;
        Assert.Equal(result.AggregateContributorPoolCents, promoted.AggregateContributorPoolCents);
        Assert.Equal(result.Net60EligibilityDate, promoted.Net60EligibilityDate);
        Assert.Equal(result.Allocations.ToArray(), promoted.Allocations.ToArray());
        Assert.Equal(-500, await migrated.BalanceAsync(LedgerAccount.ContributorPayable));
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_periods SET approved_utc = $approved WHERE period_id = $period;
                """;
            command.Parameters.AddWithValue("$approved", now.AddMinutes(4).ToString("O"));
            command.Parameters.AddWithValue("$period", preview.CarePeriodId!);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => migrated.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now.AddMinutes(3)));
        Assert.Null(await migrated.GetPayoutStateAsync(preview.BatchId));
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE care_periods SET approved_utc = $approved WHERE period_id = $period;
                """;
            command.Parameters.AddWithValue("$approved", freshReview.ReviewedUtc.ToString("O"));
            command.Parameters.AddWithValue("$period", preview.CarePeriodId!);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.True(await migrated.ReservePayoutAsync(
            preview, true, true, preview.EligibilityDate, now.AddMinutes(3)));

        await using var verified = new SqliteConnection(connectionString);
        await verified.OpenAsync();
        var version = verified.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
        var closeReview = verified.CreateCommand();
        closeReview.CommandText = """
            SELECT close_review_sha256, close_approval_sha256,
                   reviewed_utc, review_expires_utc, approved_utc
            FROM care_periods WHERE period_id = $period;
            """;
        closeReview.Parameters.AddWithValue("$period", preview.CarePeriodId!);
        await using var reader = await closeReview.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(freshReview.ReviewSha256, reader.GetString(0));
        Assert.Equal(64, reader.GetString(1).Length);
        Assert.Equal(freshReview.ReviewedUtc.ToString("O"), reader.GetString(2));
        Assert.Equal(freshReview.ExpiresUtc.ToString("O"), reader.GetString(3));
        Assert.Equal(freshReview.ReviewedUtc.ToString("O"), reader.GetString(4));
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
        Assert.Equal(15L, (long)(await version.ExecuteScalarAsync())!);
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

    private static PayoutBatchPreview Preview(string batchId, int month = 1) => new(
        batchId,
        PayoutExecutionMode.Sandbox,
        new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)).AddDays(60),
        [new PayoutItem("contributor:one", 500, Approval("contributor:one"))],
        $"care:2026-{month:D2}");

    private static PayoutRecipientApproval Approval(string contributor) =>
        PayoutRecipientApproval.Create(
            $"snapshot:{contributor.Replace(':', '-')}",
            $"{contributor.Replace(':', '-')}@personal.example.com");

    private static PayoutBatchPreview DashboardPreview(
        string batchId,
        CarePeriodResult result,
        long amountCents,
        string snapshotId) => new(
        batchId,
        PayoutExecutionMode.Sandbox,
        result.Net60EligibilityDate,
        [
            new PayoutItem(
                "contributor:one",
                amountCents,
                PayoutRecipientApproval.Create(
                    snapshotId,
                    $"{snapshotId.Replace(':', '-')}@personal.example.com")),
        ],
        PayoutBatchPreviewFactory.CarePeriodId(result));

    private async Task AccrueAsync(DurableCareRepository repository, PayoutBatchPreview preview)
    {
        PayoutContractValidation.RequireAllocationBoundPreview(preview);
        var period = preview.CarePeriodId!;
        var year = int.Parse(period.AsSpan(5, 4));
        var month = int.Parse(period.AsSpan(10, 2));
        var revenue = checked(preview.TotalCents * 5 / 2);
        Assert.Equal(preview.TotalCents * 5, revenue * 2);
        var result = await RegisterAsync(
            repository,
            new CarePeriodInput(
                year, month, revenue, 0, 0, 0, 0,
                ContributorPoolBasisPoints: 4000,
                MinimumPayoutCents: 0),
            preview.Items.Select(item => new ContributorUsageWeight(
                item.ContributorPseudonym,
                item.AmountCents)).ToArray(),
            now);
        Assert.Equal(preview.EligibilityDate, result.Net60EligibilityDate);
        Assert.Equal(preview.TotalCents, result.AggregateContributorPoolCents);
    }

    private static async Task<CarePeriodResult> RegisterAsync(
        DurableCareRepository repository,
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage,
        DateTimeOffset reviewedUtc)
    {
        var review = DurableCareRepository.ReviewCarePeriodClose(
            input, usage, reviewedUtc);
        return await repository.RegisterReviewedCarePeriodAsync(
            review, true, reviewedUtc);
    }

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
            ],
            "care:2026-01");
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
