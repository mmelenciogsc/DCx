namespace DCx.Core.Reliability;

public enum BackupRecoveryHandoffStatus
{
    Opened,
}

public sealed record BackupRecoveryHandoffResult(
    BackupRecoveryHandoffStatus Status,
    int ProcessStarts,
    bool TargetsCurrentExecutable,
    bool UsesExactRecoveryArgument,
    bool UsesShellExecution,
    bool RequestsElevation,
    bool BackupCreated,
    bool ApplicationCloseRequested,
    int InstallerProcessesStarted,
    int ProductionActions);

public static class BackupRecoveryHandoffResultPolicy
{
    public const string RecoveryArgument = "--recovery";

    public static void Require(BackupRecoveryHandoffResult? result)
    {
        if (result is null || result.Status != BackupRecoveryHandoffStatus.Opened ||
            result.ProcessStarts != 1 || !result.TargetsCurrentExecutable ||
            !result.UsesExactRecoveryArgument || result.UsesShellExecution ||
            result.RequestsElevation || result.BackupCreated ||
            result.ApplicationCloseRequested || result.InstallerProcessesStarted != 0 ||
            result.ProductionActions != 0)
        {
            throw new InvalidOperationException("backup_recovery_handoff_result_invalid");
        }
    }
}

public interface IBackupRecoveryHandoff
{
    BackupRecoveryHandoffResult Open();
}
