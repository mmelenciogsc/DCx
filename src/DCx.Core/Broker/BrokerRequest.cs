namespace DCx.Core.Broker;

public sealed record BrokerRequest(BrokerOperation Operation, string? TargetPath = null);
