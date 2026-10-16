namespace DCx.Core.Control;

public sealed record AgentControlRequest(
    Guid RequestId,
    string Purpose,
    string AllowedOrigin,
    DateTimeOffset RequestedAtUtc);

