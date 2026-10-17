namespace DCx.Core.Finance;

public sealed record CareOperationsHealth(
    string StatusCode,
    int Pending,
    int Due,
    int Blocked,
    int Completed,
    int MaximumAttemptCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset? NextAttemptUtc,
    bool CanProcessDue);

public sealed record CareOperationsProcessResult(
    int Scanned,
    int Completed,
    int StillPending,
    int Blocked,
    int ProviderCalls,
    CareOperationsHealth Health);

public interface ICareOperationsSource
{
    Task<CareOperationsHealth> RefreshAsync(CancellationToken cancellationToken = default);

    Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default);
}

public static class CareOperationsPolicy
{
    public const int MaximumAggregateCount = 1_000_000;
    public const int MaximumProcessBatch = 100;

    public static void RequireHealth(CareOperationsHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.StatusCode is not (
                "not_configured" or
                "clear" or
                "retry_due" or
                "retry_scheduled" or
                "operator_action_required") ||
            !IsCount(health.Pending) ||
            !IsCount(health.Due) ||
            !IsCount(health.Blocked) ||
            !IsCount(health.Completed) ||
            !IsCount(health.MaximumAttemptCount) ||
            health.Due > health.Pending ||
            health.OldestPendingUtc is { Offset: var oldestOffset } &&
                oldestOffset != TimeSpan.Zero ||
            health.NextAttemptUtc is { Offset: var nextOffset } &&
                nextOffset != TimeSpan.Zero ||
            health.Pending == 0 &&
                (health.Due != 0 || health.OldestPendingUtc is not null ||
                 health.NextAttemptUtc is not null || health.CanProcessDue) ||
            health.Pending > 0 &&
                (health.OldestPendingUtc is null || health.NextAttemptUtc is null) ||
            health.CanProcessDue && health.Due == 0 ||
            health.Blocked > 0 && health.StatusCode != "operator_action_required" ||
            health.StatusCode == "not_configured" &&
                (health.Pending != 0 || health.Blocked != 0 || health.Completed != 0 ||
                 health.MaximumAttemptCount != 0) ||
            health.StatusCode == "clear" && (health.Pending != 0 || health.Blocked != 0) ||
            health.StatusCode == "retry_due" && health.Due == 0 ||
            health.StatusCode == "retry_scheduled" && (health.Pending == 0 || health.Due != 0) ||
            health.StatusCode == "operator_action_required" && health.Blocked == 0)
        {
            throw new InvalidOperationException("The CARE operations health contract is invalid.");
        }
    }

    public static void RequireProcessResult(CareOperationsProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireHealth(result.Health);
        if (result.Scanned is < 0 or > MaximumProcessBatch ||
            result.Completed < 0 || result.StillPending < 0 || result.Blocked < 0 ||
            result.Scanned != result.Completed + result.StillPending + result.Blocked ||
            result.ProviderCalls != 0)
        {
            throw new InvalidOperationException("The CARE operations process result is invalid.");
        }
    }

    private static bool IsCount(int value) => value is >= 0 and <= MaximumAggregateCount;
}

public sealed class UnavailableCareOperationsSource : ICareOperationsSource
{
    public static UnavailableCareOperationsSource Instance { get; } = new();

    private UnavailableCareOperationsSource()
    {
    }

    public Task<CareOperationsHealth> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CareOperationsHealth(
            "not_configured", 0, 0, 0, 0, 0, null, null, false));
    }

    public Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedHealth);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "CARE verified-event processing is not configured.");
    }
}
