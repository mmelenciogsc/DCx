using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class RecoveryReturnHandoffTests
{
    [Fact]
    public void ResultPolicy_AdmitsOnlyExactLeastAuthorityReturn()
    {
        var valid = Valid();

        RecoveryReturnHandoffResultPolicy.Require(valid);
        var invalid = new RecoveryReturnHandoffResult?[]
        {
            null,
            valid with { Status = (RecoveryReturnHandoffStatus)99 },
            valid with { ProcessStarts = 0 },
            valid with { ProcessStarts = 2 },
            valid with { TargetsCurrentExecutable = false },
            valid with { UsesNoArguments = false },
            valid with { UsesShellExecution = true },
            valid with { RequestsElevation = true },
            valid with { RecoveryWindowCloseExpected = false },
            valid with { PackageManagerInvocations = 1 },
            valid with { InstallerProcessesStarted = 1 },
            valid with { ProductionActions = 1 },
        };

        foreach (var result in invalid)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RecoveryReturnHandoffResultPolicy.Require(result));
            Assert.Equal("recovery_return_handoff_result_invalid", exception.Message);
        }
    }

    private static RecoveryReturnHandoffResult Valid() => new(
        RecoveryReturnHandoffStatus.Opened,
        ProcessStarts: 1,
        TargetsCurrentExecutable: true,
        UsesNoArguments: true,
        UsesShellExecution: false,
        RequestsElevation: false,
        RecoveryWindowCloseExpected: true,
        PackageManagerInvocations: 0,
        InstallerProcessesStarted: 0,
        ProductionActions: 0);
}
