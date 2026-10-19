namespace DCx.Core.Finance;

public sealed record CareContributorPeriodStatus(
    string PeriodId,
    DateOnly EarningPeriodClose,
    DateOnly Net60EligibilityDate,
    long AllocationCents,
    long PaidCents,
    long OutstandingCents,
    string StatusCode);

public sealed record CareContributorLocalNotice(
    string NoticeId,
    string PeriodId,
    long AmountCents,
    string NoticeCode,
    DateTimeOffset RecordedUtc);

public sealed record CareContributorDashboardSnapshot(
    string StatusCode,
    string? ContributorPseudonym,
    string Currency,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<CareContributorPeriodStatus> Periods,
    IReadOnlyList<CareContributorLocalNotice> Notices);

public interface ICareContributorDashboardSource
{
    Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default);
}

public static class CareContributorDashboardPolicy
{
    public const int MaximumPeriods = 120;
    public const int MaximumNotices = 120;
    public const string NotConfigured = "not_configured";
    public const string Available = "available";
    public const string HeldBelowThreshold = "held_below_threshold";
    public const string Withheld = "withheld";
    public const string Eligible = "eligible";
    public const string Pending = "pending";
    public const string PartiallyPaid = "partially_paid";
    public const string Paid = "paid";
    public const string FailedPayoutHoldRetained = "failed_payout_hold_retained";
    public const string RecipientUpdateRequested = "recipient_update_requested";
    public const string RetryIntentCancelled = "retry_intent_cancelled";

    public static void RequireSnapshot(CareContributorDashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Periods);
        ArgumentNullException.ThrowIfNull(snapshot.Notices);
        if (snapshot.StatusCode is not (NotConfigured or Available) ||
            !string.Equals(snapshot.Currency, "USD", StringComparison.Ordinal) ||
            snapshot.GeneratedUtc.Offset != TimeSpan.Zero ||
            snapshot.Periods.Count > MaximumPeriods ||
            snapshot.Notices.Count > MaximumNotices)
        {
            throw new InvalidOperationException("The CARE contributor dashboard contract is invalid.");
        }

        if (snapshot.StatusCode == NotConfigured)
        {
            if (snapshot.ContributorPseudonym is not null || snapshot.Periods.Count != 0 ||
                snapshot.Notices.Count != 0)
            {
                throw new InvalidOperationException(
                    "An unavailable CARE contributor dashboard cannot imply contributor state.");
            }

            return;
        }

        if (snapshot.ContributorPseudonym is null)
        {
            throw new InvalidOperationException(
                "An available CARE contributor dashboard requires an authenticated contributor identity.");
        }
        PayPalSandboxValidation.RequireContributorPseudonym(snapshot.ContributorPseudonym);

        var periodIds = new HashSet<string>(StringComparer.Ordinal);
        var periodsById = new Dictionary<string, CareContributorPeriodStatus>(StringComparer.Ordinal);
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

            periodsById.Add(period.PeriodId, period);
            priorClose = period.EarningPeriodClose;
        }

        var noticeIds = new HashSet<string>(StringComparer.Ordinal);
        CareContributorLocalNotice? priorNotice = null;
        foreach (var notice in snapshot.Notices)
        {
            ArgumentNullException.ThrowIfNull(notice);
            PayoutContractValidation.RequireCarePeriodId(notice.PeriodId);
            var knownPeriod = periodsById.GetValueOrDefault(notice.PeriodId);
            if (!PayoutContractValidation.IsLowerSha256(notice.NoticeId) ||
                !noticeIds.Add(notice.NoticeId) || knownPeriod is null ||
                notice.AmountCents <= 0 || notice.AmountCents > knownPeriod.AllocationCents ||
                notice.NoticeCode is not (
                    FailedPayoutHoldRetained or
                    RecipientUpdateRequested or
                    RetryIntentCancelled) ||
                notice.RecordedUtc == default || notice.RecordedUtc.Offset != TimeSpan.Zero ||
                notice.RecordedUtc > snapshot.GeneratedUtc ||
                priorNotice is not null &&
                (notice.RecordedUtc > priorNotice.RecordedUtc ||
                 notice.RecordedUtc == priorNotice.RecordedUtc &&
                 string.CompareOrdinal(notice.NoticeId, priorNotice.NoticeId) <= 0))
            {
                throw new InvalidOperationException(
                    "The CARE contributor local-notice contract is invalid.");
            }

            priorNotice = notice;
        }
    }

    private static bool StatusMatches(
        CareContributorPeriodStatus period,
        DateTimeOffset generatedUtc) => period.StatusCode switch
        {
            HeldBelowThreshold or Withheld =>
                period.PaidCents == 0 && period.OutstandingCents == period.AllocationCents,
            Eligible =>
                period.PaidCents == 0 &&
                period.OutstandingCents == period.AllocationCents &&
                DateOnly.FromDateTime(generatedUtc.UtcDateTime) >= period.Net60EligibilityDate,
            Pending =>
                period.OutstandingCents > 0 &&
                DateOnly.FromDateTime(generatedUtc.UtcDateTime) >= period.Net60EligibilityDate,
            PartiallyPaid =>
                period.PaidCents > 0 && period.OutstandingCents > 0 &&
                DateOnly.FromDateTime(generatedUtc.UtcDateTime) >= period.Net60EligibilityDate,
            Paid =>
                period.PaidCents == period.AllocationCents && period.OutstandingCents == 0 &&
                DateOnly.FromDateTime(generatedUtc.UtcDateTime) >= period.Net60EligibilityDate,
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
            [],
            []));
    }
}
