namespace DCx.Core.Finance;

public sealed record CareContributorPeriodStatus(
    string PeriodId,
    DateOnly EarningPeriodClose,
    DateOnly Net60EligibilityDate,
    long AllocationCents,
    long PaidCents,
    long OutstandingCents,
    string StatusCode);

public sealed record CareContributorDashboardSnapshot(
    string StatusCode,
    string? ContributorPseudonym,
    string Currency,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<CareContributorPeriodStatus> Periods);

public interface ICareContributorDashboardSource
{
    Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default);
}

public static class CareContributorDashboardPolicy
{
    public const int MaximumPeriods = 120;
    public const string NotConfigured = "not_configured";
    public const string Available = "available";
    public const string HeldBelowThreshold = "held_below_threshold";
    public const string Withheld = "withheld";
    public const string Eligible = "eligible";
    public const string Pending = "pending";
    public const string PartiallyPaid = "partially_paid";
    public const string Paid = "paid";

    public static void RequireSnapshot(CareContributorDashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Periods);
        if (snapshot.StatusCode is not (NotConfigured or Available) ||
            !string.Equals(snapshot.Currency, "USD", StringComparison.Ordinal) ||
            snapshot.GeneratedUtc.Offset != TimeSpan.Zero ||
            snapshot.Periods.Count > MaximumPeriods)
        {
            throw new InvalidOperationException("The CARE contributor dashboard contract is invalid.");
        }

        if (snapshot.StatusCode == NotConfigured)
        {
            if (snapshot.ContributorPseudonym is not null || snapshot.Periods.Count != 0)
            {
                throw new InvalidOperationException(
                    "An unavailable CARE contributor dashboard cannot imply contributor state.");
            }

            return;
        }

        if (snapshot.ContributorPseudonym is null || snapshot.Periods.Count == 0)
        {
            throw new InvalidOperationException(
                "An available CARE contributor dashboard requires one bounded contributor history.");
        }
        PayPalSandboxValidation.RequireContributorPseudonym(snapshot.ContributorPseudonym);

        var periodIds = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? priorClose = null;
        foreach (var period in snapshot.Periods)
        {
            ArgumentNullException.ThrowIfNull(period);
            PayoutContractValidation.RequireCarePeriodId(period.PeriodId);
            var expectedId = $"care:{period.EarningPeriodClose.Year:D4}-{period.EarningPeriodClose.Month:D2}";
            if (!string.Equals(period.PeriodId, expectedId, StringComparison.Ordinal) ||
                period.EarningPeriodClose.Day != DateTime.DaysInMonth(
                    period.EarningPeriodClose.Year, period.EarningPeriodClose.Month) ||
                period.Net60EligibilityDate != period.EarningPeriodClose.AddDays(60) ||
                !periodIds.Add(period.PeriodId) ||
                priorClose is not null && period.EarningPeriodClose >= priorClose ||
                period.AllocationCents <= 0 ||
                period.PaidCents < 0 || period.OutstandingCents < 0)
            {
                throw new InvalidOperationException(
                    "The CARE contributor period contract is invalid.");
            }

            long total;
            try
            {
                total = checked(period.PaidCents + period.OutstandingCents);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException(
                    "The CARE contributor period amount is invalid.", exception);
            }

            if (total != period.AllocationCents || !StatusMatches(period, snapshot.GeneratedUtc))
            {
                throw new InvalidOperationException(
                    "The CARE contributor period status conflicts with its amounts or eligibility date.");
            }

            priorClose = period.EarningPeriodClose;
        }
    }

    private static bool StatusMatches(
        CareContributorPeriodStatus period,
        DateTimeOffset generatedUtc) => period.StatusCode switch
    {
        HeldBelowThreshold or Withheld =>
            period.PaidCents == 0 && period.OutstandingCents == period.AllocationCents,
        Eligible or Pending =>
            period.PaidCents == 0 &&
            period.OutstandingCents == period.AllocationCents &&
            DateOnly.FromDateTime(generatedUtc.UtcDateTime) >= period.Net60EligibilityDate,
        PartiallyPaid => period.PaidCents > 0 && period.OutstandingCents > 0,
        Paid => period.PaidCents == period.AllocationCents && period.OutstandingCents == 0,
        _ => false,
    };
}

public sealed class UnavailableCareContributorDashboardSource : ICareContributorDashboardSource
{
    public static UnavailableCareContributorDashboardSource Instance { get; } = new();

    private UnavailableCareContributorDashboardSource()
    {
    }

    public Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CareContributorDashboardSnapshot(
            CareContributorDashboardPolicy.NotConfigured,
            null,
            "USD",
            DateTimeOffset.UtcNow,
            []));
    }
}
