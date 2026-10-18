using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class CareContributorDashboardTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-18T02:00:00Z");

    [Fact]
    public void Contract_IsBoundedAndExcludesProviderRecipientAndAccountFields()
    {
        Assert.Equal(
            ["StatusCode", "ContributorPseudonym", "Currency", "GeneratedUtc", "Periods"],
            typeof(CareContributorDashboardSnapshot)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            [
                "PeriodId", "EarningPeriodClose", "Net60EligibilityDate",
                "AllocationCents", "PaidCents", "OutstandingCents", "StatusCode",
            ],
            typeof(CareContributorPeriodStatus)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
        var propertyNames = string.Join(
            ' ',
            typeof(CareContributorDashboardSnapshot).GetProperties()
                .Concat(typeof(CareContributorPeriodStatus).GetProperties())
                .Select(property => property.Name));
        Assert.DoesNotContain("Email", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PayPal", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Account", propertyNames, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Recipient", propertyNames, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_AcceptsOrderedHeldEligiblePendingPartialAndPaidHistory()
    {
        var snapshot = Snapshot(
            Period(2026, 5, 700, 0, 700, CareContributorDashboardPolicy.Withheld),
            Period(2026, 4, 600, 0, 600, CareContributorDashboardPolicy.HeldBelowThreshold),
            Period(2026, 3, 500, 0, 500, CareContributorDashboardPolicy.Pending),
            Period(2026, 2, 400, 0, 400, CareContributorDashboardPolicy.Eligible),
            Period(2026, 1, 300, 100, 200, CareContributorDashboardPolicy.PartiallyPaid),
            Period(2025, 12, 200, 200, 0, CareContributorDashboardPolicy.Paid));

        CareContributorDashboardPolicy.RequireSnapshot(snapshot);
    }

    [Fact]
    public void Policy_RejectsIdentityOrderingDateAmountAndStatusSubstitution()
    {
        var exact = Snapshot(Period(
            2026, 1, 500, 0, 500, CareContributorDashboardPolicy.Eligible));
        Assert.ThrowsAny<Exception>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                ContributorPseudonym = "invalid contributor",
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                Periods = [exact.Periods[0], exact.Periods[0]],
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                Periods = [exact.Periods[0] with { Net60EligibilityDate = new DateOnly(2026, 4, 2) }],
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                Periods = [exact.Periods[0] with { PaidCents = 1 }],
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                Periods = [exact.Periods[0] with { StatusCode = CareContributorDashboardPolicy.Paid }],
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CareContributorDashboardPolicy.RequireSnapshot(exact with
            {
                Periods = [Period(2026, 9, 500, 0, 500, CareContributorDashboardPolicy.Eligible)],
            }));
    }

    [Fact]
    public async Task UnavailableSource_DoesNotInventContributorOrPeriodState()
    {
        var snapshot = await UnavailableCareContributorDashboardSource.Instance.LoadAsync();

        CareContributorDashboardPolicy.RequireSnapshot(snapshot);
        Assert.Equal(CareContributorDashboardPolicy.NotConfigured, snapshot.StatusCode);
        Assert.Null(snapshot.ContributorPseudonym);
        Assert.Empty(snapshot.Periods);
    }

    private static CareContributorDashboardSnapshot Snapshot(
        params CareContributorPeriodStatus[] periods) => new(
        CareContributorDashboardPolicy.Available,
        "contributor:fixture",
        "USD",
        Now,
        periods);

    private static CareContributorPeriodStatus Period(
        int year,
        int month,
        long allocation,
        long paid,
        long outstanding,
        string status)
    {
        var close = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return new CareContributorPeriodStatus(
            $"care:{year:D4}-{month:D2}",
            close,
            close.AddDays(60),
            allocation,
            paid,
            outstanding,
            status);
    }
}
