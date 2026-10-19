using System.Diagnostics;
using System.IO;
using DCx.Core.Reliability;

namespace DCx.Desktop;

internal sealed class WindowsBackupRecoveryHandoff : IBackupRecoveryHandoff
{
    public BackupRecoveryHandoffResult Open()
    {
        var executable = Environment.ProcessPath;
        if (!IsExactCurrentExecutable(executable))
        {
            throw new InvalidOperationException("backup_recovery_executable_unavailable");
        }

        var startInfo = new ProcessStartInfo(executable!)
        {
            CreateNoWindow = false,
            UseShellExecute = false,
            Verb = string.Empty,
        };
        startInfo.ArgumentList.Add(BackupRecoveryHandoffResultPolicy.RecoveryArgument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("backup_recovery_handoff_failed");
        var result = new BackupRecoveryHandoffResult(
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
        BackupRecoveryHandoffResultPolicy.Require(result);
        return result;
    }

    private static bool IsExactCurrentExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32_767 ||
            value.Any(char.IsControl) || !Path.IsPathFullyQualified(value) ||
            !string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.GetFullPath(value));
        }
        catch
        {
            return false;
        }
    }
}
