namespace DCx.Core.Finance;

public enum LedgerAccount
{
    Cash = 0,
    SubscriptionRevenue = 1,
    RefundsAndChargebacks = 2,
    PaymentFees = 3,
    ApprovedOperatingCosts = 4,
    TaxAndRiskReserve = 5,
    ContributorPayable = 6,
    ContributorExpense = 7,
}

public sealed record LedgerPosting(LedgerAccount Account, long DebitCents, long CreditCents);

public sealed record LedgerJournalEntry(
    string EventId,
    DateTimeOffset OccurredUtc,
    string DescriptionCode,
    IReadOnlyList<LedgerPosting> Postings);

public sealed class DoubleEntryLedger
{
    private readonly Dictionary<string, LedgerJournalEntry> entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<LedgerJournalEntry> Entries => entries.Values;

    public bool Post(LedgerJournalEntry entry)
    {
        Validate(entry);
        if (entries.TryGetValue(entry.EventId, out var existing))
        {
            if (existing != entry)
            {
                throw new InvalidOperationException("A ledger event ID cannot identify different journal entries.");
            }

            return false;
        }

        entries.Add(entry.EventId, entry);
        return true;
    }

    public long Balance(LedgerAccount account) => entries.Values
        .SelectMany(entry => entry.Postings)
        .Where(posting => posting.Account == account)
        .Sum(posting => checked(posting.DebitCents - posting.CreditCents));

    public bool Reconciles() => entries.Values.All(entry =>
        entry.Postings.Sum(posting => posting.DebitCents) ==
        entry.Postings.Sum(posting => posting.CreditCents));

    private static void Validate(LedgerJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.EventId) || entry.EventId.Length > 128 ||
            string.IsNullOrWhiteSpace(entry.DescriptionCode) || entry.DescriptionCode.Length > 128 ||
            entry.Postings.Count < 2 ||
            entry.Postings.Any(posting => posting.DebitCents < 0 || posting.CreditCents < 0 ||
                                          (posting.DebitCents > 0 && posting.CreditCents > 0)) ||
            entry.Postings.Sum(posting => posting.DebitCents) != entry.Postings.Sum(posting => posting.CreditCents))
        {
            throw new InvalidOperationException("The journal entry is invalid or unbalanced.");
        }
    }
}
