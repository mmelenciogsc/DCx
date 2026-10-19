namespace DCx.Core.Reliability;

public enum RecoveryReturnHandoffStatus
{
    Opened,
}

public sealed record RecoveryReturnHandoffResult(
    RecoveryReturnHandoffStatus Status,
    int ProcessStarts,
    bool TargetsCurrentExecutable,
    bool UsesNoArguments,
    bool UsesShellExecution,
    bool RequestsElevation,
    bool RecoveryWindowCloseExpected,
    int PackageManagerInvocations,
    int InstallerProcessesStarted,
    int ProductionActions);

public static class RecoveryReturnHandoffResultPolicy
{
    public static void Require(RecoveryReturnHandoffResult? result)
    {
        if (result is null || result.Status != RecoveryReturnHandoffStatus.Opened ||
            result.ProcessStarts != 1 || !result.TargetsCurrentExecutable ||
            !result.UsesNoArguments || result.UsesShellExecution || result.RequestsElevation ||
            !result.RecoveryWindowCloseExpected || result.PackageManagerInvocations != 0 ||
            result.InstallerProcessesStarted != 0 || result.ProductionActions != 0)
        {
            throw new InvalidOperationException("recovery_return_handoff_result_invalid");
        }
    }
}

public interface IRecoveryReturnHandoff
{
    RecoveryReturnHandoffResult Open();
}
