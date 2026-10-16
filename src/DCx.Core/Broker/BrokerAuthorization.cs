namespace DCx.Core.Broker;

public sealed record BrokerAuthorization(
    bool IsAllowed,
    BrokerOperation Operation,
    string? CanonicalTargetPath,
    string Reason)
{
    public static BrokerAuthorization Allow(BrokerOperation operation, string? path, string reason) =>
        new(true, operation, path, reason);

    public static BrokerAuthorization Deny(BrokerOperation operation, string reason) =>
        new(false, operation, null, reason);
}
