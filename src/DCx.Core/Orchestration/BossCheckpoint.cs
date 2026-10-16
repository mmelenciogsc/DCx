namespace DCx.Core.Orchestration;

public sealed record BossCheckpoint(
    string RunId,
    string TaskId,
    int Attempt,
    string Status,
    BossSpecialistRole Role,
    BossRouteKind? Route,
    string RouteReason,
    int InputTokens,
    int OutputTokens,
    long ElapsedMilliseconds,
    IReadOnlyCollection<string> Evidence,
    string? FailureCode,
    DateTimeOffset RecordedAtUtc);

public interface IBossCheckpointStore
{
    Task AppendAsync(BossCheckpoint checkpoint, CancellationToken cancellationToken);
}
