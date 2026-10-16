namespace DCx.Core.Orchestration;

public interface IBossCapability
{
    string Id { get; }

    BossRouteKind RouteKind { get; }

    bool IsAvailable { get; }

    bool Supports(string capability);

    Task<BossCapabilityResult> ExecuteAsync(BossTaskContext context, CancellationToken cancellationToken);
}

public sealed record BossTaskContext(
    string TaskId,
    string DisplayName,
    BossSpecialistRole Role,
    int Attempt,
    IReadOnlyDictionary<string, BossTaskOutcome> DependencyOutcomes);

public sealed record BossCapabilityResult(
    bool Succeeded,
    IReadOnlySet<string> Evidence,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? FailureCode = null);
