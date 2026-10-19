using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class BackupRecoveryHandoffTests
{
    [Fact]
    public void ResultPolicy_AdmitsOnlyExactLeastAuthorityHandoff()
    {
        var valid = Valid();

        BackupRecoveryHandoffResultPolicy.Require(valid);
        Assert.Equal("--recovery", BackupRecoveryHandoffResultPolicy.RecoveryArgument);
        var invalid = new BackupRecoveryHandoffResult?[]
        {
            null,
            valid with { Status = (BackupRecoveryHandoffStatus)99 },
            valid with { ProcessStarts = 0 },
            valid with { ProcessStarts = 2 },
            valid with { TargetsCurrentExecutable = false },
            valid with { UsesExactRecoveryArgument = false },
            valid with { UsesShellExecution = true },
            valid with { RequestsElevation = true },
            valid with { BackupCreated = true },
            valid with { ApplicationCloseRequested = true },
            valid with { InstallerProcessesStarted = 1 },
            valid with { ProductionActions = 1 },
        };

        foreach (var result in invalid)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                BackupRecoveryHandoffResultPolicy.Require(result));
            Assert.Equal("backup_recovery_handoff_result_invalid", exception.Message);
        }
    }

    private static BackupRecoveryHandoffResult Valid() => new(
        BackupRecoveryHandoffStatus.Opened,
        ProcessStarts: 1,
        TargetsCurrentExecutable: true,
        UsesExactRecoveryArgument: true,
        UsesShellExecution: false,
        RequestsElevation: false,
        BackupCreated: false,
        ApplicationCloseRequested: false,
        InstallerProcessesStarted: 0,
        ProductionActions: 0);
}
