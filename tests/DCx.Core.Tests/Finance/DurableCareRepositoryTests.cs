using System.Security.Cryptography;
using DCx.Core.Finance;
using DCx.GlobalService;

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

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private PayPalSandboxWebhookEvent Event(string eventId, string transmissionId, string eventType, long amount) =>
        new(eventId, transmissionId, eventType, "subscription:one", amount, "USD", now);

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
}
