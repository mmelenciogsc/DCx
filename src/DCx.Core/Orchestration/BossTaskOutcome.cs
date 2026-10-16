namespace DCx.Core.Orchestration;

public enum BossTaskStatus
{
    Completed,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record BossTaskOutcome(
    string TaskId,
    BossTaskStatus Status,
    BossSpecialistRole Role,
    BossRouteKind? Route,
    string RouteReason,
    int Attempts,
    int InputTokens,
    int OutputTokens,
    TimeSpan Elapsed,
    IReadOnlySet<string> Evidence,
    string? FailureCode = null);

public sealed record BossRunResult(
    IReadOnlyList<BossTaskOutcome> Tasks,
    int InputTokens,
    int OutputTokens,
    TimeSpan Elapsed)
{
    public bool Succeeded => Tasks.All(task => task.Status == BossTaskStatus.Completed);
}
