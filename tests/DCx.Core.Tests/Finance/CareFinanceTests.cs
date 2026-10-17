using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class CareFinanceTests
{
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-01-15T00:00:00Z");

    [Fact]
    public void SandboxWebhooks_AreVerifiedIdempotentAndBalancedAcrossRefund()
    {
        var ledger = new DoubleEntryLedger();
        var processor = new PayPalSandboxWebhookProcessor(ledger, Enumerable.Repeat((byte)7, 32).ToArray());
        var activated = Event("event:active", "transmission:active", "BILLING.SUBSCRIPTION.ACTIVATED", 0);
        var payment = Event("event:payment", "transmission:payment", "PAYMENT.SALE.COMPLETED", 300);
        var refund = Event("event:refund", "transmission:refund", "PAYMENT.SALE.REFUNDED", 300);

        Assert.True(processor.Process(activated, processor.Sign(activated)));
        Assert.True(processor.GetEntitlement("subscription:one")!.Active);
        Assert.True(processor.Process(payment, processor.Sign(payment)));
        Assert.False(processor.Process(payment, processor.Sign(payment)));
        Assert.True(processor.Process(refund, processor.Sign(refund)));

        Assert.False(processor.GetEntitlement("subscription:one")!.Active);
        Assert.Equal(2, ledger.Entries.Count);
        Assert.True(ledger.Reconciles());
        Assert.Equal(0, ledger.Balance(LedgerAccount.Cash));
        Assert.Equal(-300, ledger.Balance(LedgerAccount.SubscriptionRevenue));
        Assert.Equal(300, ledger.Balance(LedgerAccount.RefundsAndChargebacks));
    }

    [Fact]
    public void SandboxWebhook_RejectsBadSignatureWrongPlanAndDuplicateEventId()
    {
        var processor = new PayPalSandboxWebhookProcessor(new DoubleEntryLedger(), Enumerable.Repeat((byte)8, 32).ToArray());
        var payment = Event("event:one", "transmission:one", "PAYMENT.SALE.COMPLETED", 300);

        Assert.Throws<InvalidOperationException>(() => processor.Process(payment, "bad"));
        Assert.True(processor.Process(payment, processor.Sign(payment)));
        var duplicateEvent = payment with { TransmissionId = "transmission:two" };
        Assert.Throws<InvalidOperationException>(() =>
            processor.Process(duplicateEvent, processor.Sign(duplicateEvent)));
        var wrongPlan = Event("event:wrong", "transmission:wrong", "PAYMENT.SALE.COMPLETED", 301);
        Assert.Throws<InvalidOperationException>(() => processor.Process(wrongPlan, processor.Sign(wrongPlan)));
        var correctedPlan = wrongPlan with { AmountCents = 300 };
        Assert.True(processor.Process(correctedPlan, processor.Sign(correctedPlan)));
    }

    [Fact]
    public void SandboxWebhook_CausalOrderingPreventsStaleAndEqualReactivation()
    {
        var processor = new PayPalSandboxWebhookProcessor(
            new DoubleEntryLedger(), Enumerable.Repeat((byte)9, 32).ToArray());
        var activated = Event("event:causal-active", "transmission:causal-active",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now };
        var cancelled = Event("event:causal-cancel", "transmission:causal-cancel",
            "BILLING.SUBSCRIPTION.CANCELLED", 0) with { OccurredUtc = now.AddMinutes(10) };
        var stale = Event("event:causal-stale", "transmission:causal-stale",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now.AddMinutes(5) };
        var equal = Event("event:causal-equal", "transmission:causal-equal",
            "BILLING.SUBSCRIPTION.ACTIVATED", 0) with { OccurredUtc = now.AddMinutes(10) };

        Assert.True(processor.Process(activated, processor.Sign(activated)));
        Assert.True(processor.Process(cancelled, processor.Sign(cancelled)));
        Assert.True(processor.Process(stale, processor.Sign(stale)));
        Assert.True(processor.Process(equal, processor.Sign(equal)));
        Assert.False(processor.GetEntitlement("subscription:one")!.Active);
        Assert.Equal(cancelled.OccurredUtc, processor.GetEntitlement("subscription:one")!.UpdatedUtc);
    }

    [Fact]
    public void CareAllocation_CapsAggregateAtFortyPercentAndSplitsByQualifiedWeight()
    {
        var input = new CarePeriodInput(
            2026, 1, 10_000, 500, 300, 1_000, 200, ContributorPoolBasisPoints: 4000, MinimumPayoutCents: 100);

        var result = CareAllocationCalculator.Calculate(input,
        [
            new ContributorUsageWeight("contributor:a", 3m),
            new ContributorUsageWeight("contributor:b", 1m),
        ]);

        Assert.Equal(8_000, result.DistributableNetProfitCents);
        Assert.Equal(3_200, result.AggregateContributorPoolCents);
        Assert.Equal(2_400, result.Allocations.Single(item => item.ContributorPseudonym == "contributor:a").AmountCents);
        Assert.Equal(800, result.Allocations.Single(item => item.ContributorPseudonym == "contributor:b").AmountCents);
        Assert.Equal(result.AggregateContributorPoolCents, result.Allocations.Sum(item => item.AmountCents));
        Assert.True(result.AggregateContributorPoolCents <= result.DistributableNetProfitCents * 40 / 100);
        Assert.Equal(new DateOnly(2026, 4, 1), result.Net60EligibilityDate);
        var ledger = new DoubleEntryLedger();
        Assert.True(ledger.Post(CareLedgerJournal.ContributorAccrual("care:2026-01", result, now)));
        Assert.True(ledger.Reconciles());
        Assert.Equal(-3_200, ledger.Balance(LedgerAccount.ContributorPayable));
    }

    [Fact]
    public void CareAllocation_ZeroOrNegativeNetProducesNoPool()
    {
        var zero = CareAllocationCalculator.Calculate(
            new CarePeriodInput(2026, 2, 1_000, 300, 100, 600, 0),
            [new ContributorUsageWeight("contributor:a", 1m)]);
        var negative = CareAllocationCalculator.Calculate(
            new CarePeriodInput(2026, 2, 1_000, 400, 200, 600, 100),
            [new ContributorUsageWeight("contributor:a", 1m)]);

        Assert.Equal(0, zero.AggregateContributorPoolCents);
        Assert.Empty(zero.Allocations);
        Assert.Equal(0, negative.DistributableNetProfitCents);
        Assert.Equal(0, negative.AggregateContributorPoolCents);
    }

    [Fact]
    public async Task PayoutCoordinator_RequiresSandboxEligibilityOwnerApprovalAndNet60()
    {
        var provider = new FixturePayoutProvider(true);
        var coordinator = new PayoutSafetyCoordinator(provider);
        var care = CareAllocationCalculator.Calculate(
            new CarePeriodInput(2026, 1, 10_000, 500, 300, 1_000, 200, MinimumPayoutCents: 100),
            [new ContributorUsageWeight("contributor:a", 1m)]);
        var preview = PayoutBatchPreviewFactory.CreateSandbox("batch:one", care);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
            preview, true, false, new DateOnly(2026, 4, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
            preview, true, true, new DateOnly(2026, 3, 31)));
        var result = await coordinator.ExecuteAsync(preview, true, true, new DateOnly(2026, 4, 1));

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
            preview, true, true, new DateOnly(2026, 4, 1)));
        var live = preview with { BatchId = "batch:live", Mode = PayoutExecutionMode.Live };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PayoutSafetyCoordinator(provider).ExecuteAsync(
            live, true, true, new DateOnly(2026, 4, 1)));
    }

    [Fact]
    public async Task PayoutCoordinator_RecordsSandboxProviderFailureWithoutLiveFallback()
    {
        var provider = new FixturePayoutProvider(false);
        var preview = new PayoutBatchPreview(
            "batch:failure",
            PayoutExecutionMode.Sandbox,
            new DateOnly(2026, 4, 1),
            [new PayoutItem("contributor:a", 1_000)]);

        var result = await new PayoutSafetyCoordinator(provider).ExecuteAsync(
            preview, true, true, new DateOnly(2026, 4, 2));

        Assert.False(result.Succeeded);
        Assert.Equal("SANDBOX_FAILED", result.ProviderStatus);
        Assert.Equal(1, provider.Calls);
    }

    private PayPalSandboxWebhookEvent Event(string eventId, string transmissionId, string type, long amount) =>
        new(eventId, transmissionId, type, "subscription:one", amount, "USD", now);

    private sealed class FixturePayoutProvider(bool succeeds) : IPayoutSandboxProvider
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
