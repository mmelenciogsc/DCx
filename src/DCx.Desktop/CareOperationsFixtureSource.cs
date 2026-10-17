using DCx.Core.Finance;

namespace DCx.Desktop;

internal sealed class CareOperationsFixtureSource : ICareOperationsSource
{
    internal const string EnvironmentVariable = "DCX_CARE_OPERATIONS_FIXTURE";
    private static readonly DateTimeOffset FixtureTime =
        DateTimeOffset.Parse("2026-10-18T01:00:00Z");
    private int processed;

    public Task<CareOperationsHealth> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CurrentHealth());
    }

    public Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        CareOperationsPolicy.RequireHealth(reviewedHealth);
        cancellationToken.ThrowIfCancellationRequested();
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException(
                "Explicit CARE operations approval is required.");
        }

        if (reviewedHealth != CurrentHealth() ||
            Interlocked.CompareExchange(ref processed, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The reviewed CARE operations state changed before processing.");
        }

        return Task.FromResult(new CareOperationsProcessResult(
            2,
            2,
            0,
            0,
            0,
            CurrentHealth()));
    }

    private CareOperationsHealth CurrentHealth() => Volatile.Read(ref processed) == 0
        ? new CareOperationsHealth(
            "retry_due", 2, 2, 0, 0, 0, FixtureTime, FixtureTime, true)
        : new CareOperationsHealth(
            "clear", 0, 0, 0, 2, 1, null, null, false);
}

internal static class CareOperationsSourceFactory
{
    public static (ICareOperationsSource Source, bool IsFixture) Create()
    {
        var fixture = string.Equals(
            Environment.GetEnvironmentVariable(CareOperationsFixtureSource.EnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        return fixture
            ? (new CareOperationsFixtureSource(), true)
            : (UnavailableCareOperationsSource.Instance, false);
    }
}
