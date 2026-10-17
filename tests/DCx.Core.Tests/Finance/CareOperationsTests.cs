using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class CareOperationsTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-18T01:00:00Z");

    [Fact]
    public void HealthContract_IsAggregateAndIdentifierFree()
    {
        Assert.Equal(
            [
                "StatusCode",
                "Pending",
                "Due",
                "Blocked",
                "Completed",
                "MaximumAttemptCount",
                "OldestPendingUtc",
                "NextAttemptUtc",
                "CanProcessDue",
            ],
            typeof(CareOperationsHealth)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public void Policy_AcceptsConsistentDueScheduledBlockedAndClearState()
    {
        CareOperationsPolicy.RequireHealth(Health("retry_due", 2, 1, 0, true));
        CareOperationsPolicy.RequireHealth(Health("retry_scheduled", 2, 0, 0, false));
        CareOperationsPolicy.RequireHealth(Health("operator_action_required", 2, 1, 1, true));
        CareOperationsPolicy.RequireHealth(new CareOperationsHealth(
            "clear", 0, 0, 0, 4, 2, null, null, false));
    }

    [Fact]
    public void Policy_RejectsInconsistentCountsTimesAndAuthority()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireHealth(Health("retry_scheduled", 1, 1, 0, false)));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireHealth(new CareOperationsHealth(
                "clear", 0, 0, 0, 0, 0, Now, null, false)));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireHealth(Health("retry_scheduled", 1, 0, 0, true)));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireHealth(Health("operator_action_required", 1, 0, 0, false)));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireHealth(Health("retry_due", 2, 1, 1, true)));
    }

    [Fact]
    public void ProcessResult_RequiresExactBoundedLocalOutcome()
    {
        CareOperationsPolicy.RequireProcessResult(new CareOperationsProcessResult(
            3,
            1,
            1,
            1,
            0,
            Health("operator_action_required", 1, 0, 1, false)));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireProcessResult(new CareOperationsProcessResult(
                3, 1, 1, 0, 0, Health("retry_scheduled", 1, 0, 0, false))));
        Assert.Throws<InvalidOperationException>(() =>
            CareOperationsPolicy.RequireProcessResult(new CareOperationsProcessResult(
                1, 1, 0, 0, 1, new CareOperationsHealth(
                    "clear", 0, 0, 0, 1, 1, null, null, false))));
    }

    [Fact]
    public async Task UnavailableSource_IsReadOnlyAndCannotImplyAnEmptyConfiguredQueue()
    {
        var health = await UnavailableCareOperationsSource.Instance.RefreshAsync();
        CareOperationsPolicy.RequireHealth(health);
        Assert.Equal("not_configured", health.StatusCode);
        Assert.False(health.CanProcessDue);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UnavailableCareOperationsSource.Instance.ProcessDueAsync(health, true));
    }

    private static CareOperationsHealth Health(
        string status,
        int pending,
        int due,
        int blocked,
        bool canProcess) => new(
        status,
        pending,
        due,
        blocked,
        3,
        2,
        pending == 0 ? null : Now,
        pending == 0 ? null : Now.AddMinutes(1),
        canProcess);
}
