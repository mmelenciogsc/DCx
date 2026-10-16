namespace DCx.Core.Broker;

public sealed record BrokerExecutionResult(
    bool Succeeded,
    int? ProcessId,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string Message);
