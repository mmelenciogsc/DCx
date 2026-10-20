using System.IO;
using System.Windows;
using System.Windows.Automation;
using DCx.Core.Reliability;
using DCx.Core.Storage;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class RecoveryWindow : Window
{
    private readonly UserDataBackupService backupService = new();
    private readonly string dataRoot = ApplicationDataRoot.Resolve();
    private readonly CancellationTokenSource lifetime = new();
    private bool operationCompleted;
    private bool returnConsumed;

    public RecoveryWindow()
    {
        InitializeComponent();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        BackupPathBox.Text = Path.Combine(
            string.IsNullOrWhiteSpace(documents) ? AppContext.BaseDirectory : documents,
            $"DCx-backup-{DateTime.UtcNow:yyyyMMdd}.dcxbackup");
        Loaded += (_, _) => BackupPathBox.Focus();
        Closed += (_, _) => lifetime.Cancel();
    }

    private void OnChooseDestinationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose DCx backup destination",
            Filter = "DCx backup (*.dcxbackup)|*.dcxbackup",
            AddExtension = true,
            DefaultExt = ".dcxbackup",
            FileName = Path.GetFileName(BackupPathBox.Text),
        };
        if (dialog.ShowDialog(this) == true) BackupPathBox.Text = dialog.FileName;
    }

    private void OnChooseExistingClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an existing DCx backup",
            Filter = "DCx backup (*.dcxbackup)|*.dcxbackup|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) BackupPathBox.Text = dialog.FileName;
    }

    private async void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetBackupPath(out var path)) return;
        var succeeded = false;
        SetBusy(true, "Checking that DCx data is not in use.");
        try
        {
            using var lease = ApplicationDataOperationLease.Acquire(dataRoot, "creating a backup");
            var manifest = await backupService.CreateAsync(dataRoot, path, lifetime.Token);
            operationCompleted = true;
            succeeded = true;
            SetStatus($"Backup complete. {manifest.Entries.Count} files were written and verified.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Backup was not created. {exception.Message}");
        }
        finally
        {
            SetBusy(false);
            if (succeeded) ReturnToDcxButton.Focus();
        }
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (RestoreApprovalBox.IsChecked != true)
        {
            SetStatus("Restore was not started. Check the replacement approval box first.");
            RestoreApprovalBox.Focus();
            return;
        }

        if (!TryGetBackupPath(out var path)) return;
        var succeeded = false;
        SetBusy(true, "Checking the backup and confirming DCx data is not in use.");
        try
        {
            using var lease = ApplicationDataOperationLease.Acquire(dataRoot, "restoring a backup");
            var result = await backupService.RestoreAsync(path, dataRoot, lifetime.Token);
            RestoreApprovalBox.IsChecked = false;
            operationCompleted = true;
            succeeded = true;
            SetStatus($"Restore complete. {result.FileCount} files and {result.TotalBytes} bytes were verified.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Restore was not completed. {exception.Message}");
        }
        finally
        {
            SetBusy(false);
            if (succeeded) ReturnToDcxButton.Focus();
        }
    }

    private async void OnVerifyBackupClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetBackupPath(out var path)) return;
        SetBusy(true, "Verifying the selected backup without changing any data.");
        try
        {
            var result = await backupService.VerifyAsync(path, lifetime.Token);
            SetStatus(
                $"Backup verified. {result.FileCount} files and {result.TotalBytes} bytes passed manifest, path, length, and SHA-256 checks. No data was changed.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus(
                "Backup verification failed. The selected archive did not pass DCx manifest, path, length, or SHA-256 checks. No data was changed.");
        }
        finally
        {
            SetBusy(false);
            VerifyBackupButton.Focus();
        }
    }

    private void OnReturnToDcxClick(object sender, RoutedEventArgs e)
    {
        if (!operationCompleted || returnConsumed)
        {
            SetStatus("Return to DCx is unavailable until a backup or restore succeeds.");
            CreateBackupButton.Focus();
            return;
        }

        returnConsumed = true;
        SetBusy(true, "Returning to normal DCx. No package action will run.");
        try
        {
            var result = new WindowsRecoveryReturnHandoff().Open();
            RecoveryReturnHandoffResultPolicy.Require(result);
            Close();
        }
        catch
        {
            returnConsumed = false;
            SetBusy(false);
            SetStatus(
                "Normal DCx could not open safely. Recovery remains open and no package action occurred.");
            ReturnToDcxButton.Focus();
        }
    }

    private bool TryGetBackupPath(out string path)
    {
        path = BackupPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path)) return true;
        SetStatus("Enter or choose a backup file path first.");
        BackupPathBox.Focus();
        return false;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        CreateBackupButton.IsEnabled = !busy;
        VerifyBackupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
        RestoreApprovalBox.IsEnabled = !busy;
        ChooseDestinationButton.IsEnabled = !busy;
        ChooseExistingButton.IsEnabled = !busy;
        BackupPathBox.IsEnabled = !busy;
        ReturnToDcxButton.IsEnabled = !busy && operationCompleted && !returnConsumed;
        if (status is not null) SetStatus(status);
    }

    private void SetStatus(string status)
    {
        RecoveryStatusText.Text = status;
        AutomationProperties.SetName(RecoveryStatusText, status);
    }
}
