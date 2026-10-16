namespace DCx.Core.Control;

public sealed record AgentActionLease(
    Guid LeaseId,
    Guid TabId,
    string AllowedOrigin,
    long NavigationGeneration,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

