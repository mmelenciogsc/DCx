namespace DCx.Core.Finance;

public static class CareLedgerJournal
{
    public static LedgerJournalEntry ContributorAccrual(
        string eventId,
        CarePeriodResult result,
        DateTimeOffset occurredUtc) =>
        new(
            eventId,
            occurredUtc,
            "contributor_pool_accrual",
            [
                new LedgerPosting(LedgerAccount.ContributorExpense, result.AggregateContributorPoolCents, 0),
                new LedgerPosting(LedgerAccount.ContributorPayable, 0, result.AggregateContributorPoolCents),
            ]);

    public static LedgerJournalEntry SandboxPayout(
        string eventId,
        PayoutBatchPreview preview,
        DateTimeOffset occurredUtc) =>
        new(
            eventId,
            occurredUtc,
            "sandbox_contributor_payout",
            [
                new LedgerPosting(LedgerAccount.ContributorPayable, preview.TotalCents, 0),
                new LedgerPosting(LedgerAccount.Cash, 0, preview.TotalCents),
            ]);
}

public static class PayoutBatchPreviewFactory
{
    public static PayoutBatchPreview CreateSandbox(string batchId, CarePeriodResult result) =>
        new(
            batchId,
            PayoutExecutionMode.Sandbox,
            result.Net60EligibilityDate,
            result.Allocations
                .Where(item => !item.HeldBelowThreshold && item.AmountCents > 0)
                .Select(item => new PayoutItem(item.ContributorPseudonym, item.AmountCents))
                .ToArray());
}
