using DCx.Core.Finance;

namespace DCx.Desktop;

internal sealed class CareContributorDashboardFixtureSource : ICareContributorDashboardSource
{
    internal const string EnvironmentVariable = "DCX_CARE_CONTRIBUTOR_DASHBOARD_FIXTURE";
    private static readonly DateTimeOffset FixtureTime =
        DateTimeOffset.Parse("2026-10-18T02:00:00Z");

    public Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new CareContributorDashboardSnapshot(
            CareContributorDashboardPolicy.Available,
            "contributor:dashboard-fixture",
            "USD",
            FixtureTime,
            [
                Period(2026, 5, 700, 0, 700, CareContributorDashboardPolicy.Withheld),
                Period(2026, 4, 600, 0, 600, CareContributorDashboardPolicy.HeldBelowThreshold),
                Period(2026, 3, 500, 0, 500, CareContributorDashboardPolicy.Pending),
                Period(2026, 2, 400, 100, 300, CareContributorDashboardPolicy.PartiallyPaid),
                Period(2026, 1, 300, 300, 0, CareContributorDashboardPolicy.Paid),
            ],
            [
                new CareContributorLocalNotice(
                    new string('a', 64),
                    "care:2026-05",
                    700,
                    CareContributorDashboardPolicy.RecipientUpdateRequested,
                    DateTimeOffset.Parse("2026-10-18T01:20:00Z")),
            ]);
        CareContributorDashboardPolicy.RequireSnapshot(snapshot);
        return Task.FromResult(snapshot);
    }

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

internal static class CareContributorDashboardSourceFactory
{
    public static (ICareContributorDashboardSource Source, bool IsFixture) Create()
    {
        var fixture = string.Equals(
            Environment.GetEnvironmentVariable(
                CareContributorDashboardFixtureSource.EnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        return fixture
            ? (new CareContributorDashboardFixtureSource(), true)
            : (UnavailableCareContributorDashboardSource.Instance, false);
    }
}
