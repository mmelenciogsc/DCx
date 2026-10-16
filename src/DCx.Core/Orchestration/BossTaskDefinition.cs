namespace DCx.Core.Orchestration;

public sealed record BossTaskDefinition(
    string Id,
    string DisplayName,
    BossSpecialistRole Role,
    string RequiredCapability,
    IReadOnlyList<string> Dependencies,
    IReadOnlySet<string> RequiredEvidence,
    BossTaskBudget Budget,
    bool RequiresModel = false,
    bool AllowRemoteProvider = false);
