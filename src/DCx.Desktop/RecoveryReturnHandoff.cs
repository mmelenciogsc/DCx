using System.Diagnostics;
using DCx.Core.Reliability;

namespace DCx.Desktop;

internal sealed class WindowsRecoveryReturnHandoff : IRecoveryReturnHandoff
{
    public RecoveryReturnHandoffResult Open()
    {
        var startInfo = new ProcessStartInfo(CurrentExecutableProcessPolicy.Require())
        {
            CreateNoWindow = false,
            UseShellExecute = false,
            Verb = string.Empty,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("recovery_return_handoff_failed");
        var result = new RecoveryReturnHandoffResult(
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
        RecoveryReturnHandoffResultPolicy.Require(result);
        return result;
    }
}
