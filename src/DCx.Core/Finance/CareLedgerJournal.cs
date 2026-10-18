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
        long paidCents,
        DateTimeOffset occurredUtc) =>
        new(
            eventId,
            occurredUtc,
            "sandbox_contributor_payout",
            [
                new LedgerPosting(LedgerAccount.ContributorPayable, paidCents, 0),
                new LedgerPosting(LedgerAccount.Cash, 0, paidCents),
            ]);

    public static LedgerJournalEntry SandboxPayoutReturn(
        string eventId,
        long returnedCents,
        DateTimeOffset occurredUtc)
    {
        if (returnedCents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(returnedCents));
        }

        return new LedgerJournalEntry(
            eventId,
            occurredUtc,
            "sandbox_contributor_payout_return",
            [
                new LedgerPosting(LedgerAccount.Cash, returnedCents, 0),
                new LedgerPosting(LedgerAccount.ContributorPayable, 0, returnedCents),
            ]);
    }
}

public static class PayoutBatchPreviewFactory
{
    public static string CarePeriodId(CarePeriodResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var value = $"care:{result.Year:D4}-{result.Month:D2}";
        PayoutContractValidation.RequireCarePeriodId(value);
        return value;
    }

    public static PayoutBatchPreview CreateSandbox(
        string batchId,
        CarePeriodResult result,
        IReadOnlyDictionary<string, PayoutRecipientApproval> recipientApprovals)
    {
        ArgumentNullException.ThrowIfNull(recipientApprovals);
        var payable = result.Allocations
            .Where(item => !item.HeldBelowThreshold && item.AmountCents > 0)
            .ToArray();
        if (payable.Length != recipientApprovals.Count ||
            payable.Any(item => !recipientApprovals.ContainsKey(item.ContributorPseudonym)))
        {
            throw new InvalidOperationException(
                "Every payable contributor requires one exact recipient approval snapshot.");
        }

        return new PayoutBatchPreview(
            batchId,
            PayoutExecutionMode.Sandbox,
            result.Net60EligibilityDate,
            payable
                .Select(item => new PayoutItem(
                    item.ContributorPseudonym,
                    item.AmountCents,
                    recipientApprovals[item.ContributorPseudonym]))
                .ToArray(),
            CarePeriodId(result));
    }
}
