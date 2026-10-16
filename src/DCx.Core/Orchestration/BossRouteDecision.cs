namespace DCx.Core.Orchestration;

public sealed record BossRouteDecision(IBossCapability? Capability, string Reason)
{
    public bool IsAvailable => Capability is not null;
}
