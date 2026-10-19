using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using DCx.Core.Reliability;

namespace DCx.Desktop;

public partial class PackageRemovalWindow : Window
{
    private readonly IPackageRemovalWorkflow workflow;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private bool completed;

    public PackageRemovalWindow(IPackageRemovalWorkflow workflow)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        InitializeComponent();
        ConfigureSource();
        Loaded += (_, _) => HeadingText.Focus();
    }

    private void ConfigureSource()
    {
        ReviewDetailsText.Text = workflow.Review is null
            ? "DCx is running without Windows package identity. There is no current MSIX package to remove."
            : FormatReview(workflow.Review);
        AutomationProperties.SetName(ReviewDetailsText, ReviewDetailsText.Text);

        switch (workflow.SourceKind)
        {
            case PackageRemovalSourceKind.GeneratedFixture:
                BoundaryText.Text =
                    "Deterministic local validation fixture. It exercises exact review, two acknowledgments, denial focus, and one-use result handling. It cannot invoke PackageManager, close DCx, remove data or dependencies, elevate, or perform a production action.";
                SetStatus(
                    "Ready for uninstall interaction validation. Removal permitted no. PackageManager invocations 0. Production actions 0.");
                break;
            case PackageRemovalSourceKind.ApprovedCurrentPackage:
                BoundaryText.Text =
                    "Approved current-package workflow. Starting removal asks Windows to remove only this package for the current user. Windows may close DCx and the operation cannot be canceled after API entry.";
                ExecuteButton.Content = "_Start uninstall";
                AutomationProperties.SetName(ExecuteButton, "Start uninstalling the exact current DCx package");
                AutomationProperties.SetHelpText(
                    ExecuteButton,
                    "Requires both acknowledgments. Windows may close DCx and removal cannot be canceled after it starts.");
                SetStatus("Review the exact identity and both acknowledgments before starting uninstall.");
                break;
            default:
                BoundaryText.Text = workflow.Availability switch
                {
                    PackageRemovalAvailability.NotPackaged =>
                        "DCx is not running with Windows package identity. PackageManager removal is unavailable and no uninstall action can start.",
                    _ =>
                        "The current package identity is displayed for review, but no production identity and signer policy is approved. The PackageManager removal executor is not composed and no uninstall action can start.",
                };
                ConsequencesApprovalBox.IsEnabled = false;
                BackupApprovalBox.IsEnabled = false;
                ExecuteButton.IsEnabled = false;
                SetStatus(
                    "Uninstall is unavailable in this build. PackageManager invocations 0. Application close requests 0. Production actions 0.");
                break;
        }

        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
    }

    private async void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        if (workflow.Availability != PackageRemovalAvailability.Available)
        {
            SetStatus("Uninstall is unavailable. No package removal, application close, or production action occurred.");
            HeadingText.Focus();
            return;
        }

        if (ConsequencesApprovalBox.IsChecked != true)
        {
            SetStatus(
                "Uninstall was not started. Acknowledge the close, noncancellation, data, and dependency consequences first.");
            ConsequencesApprovalBox.Focus();
            return;
        }

        if (BackupApprovalBox.IsChecked != true)
        {
            SetStatus(
                "Uninstall was not started. Acknowledge your separate backup choice first.");
            BackupApprovalBox.Focus();
            return;
        }

        var review = workflow.Review;
        if (review is null)
        {
            SetStatus("Uninstall was not started because the exact current-package review is unavailable.");
            HeadingText.Focus();
            return;
        }

        SetBusy(true);
        SetStatus(workflow.SourceKind == PackageRemovalSourceKind.GeneratedFixture
            ? "Validating the one-use uninstall interaction. PackageManager will not be invoked."
            : "Starting current-user package removal. Windows may close DCx. The operation cannot be canceled after API entry.");
        try
        {
            var result = await workflow.ExecuteAsync(
                review.ReviewSha256,
                DateTimeOffset.UtcNow,
                consequencesAcknowledged: true,
                backupChoiceAcknowledged: true,
                lifetime.Token);
            PackageRemovalWorkflowResultPolicy.Require(review, result);
            completed = true;
            ConsequencesApprovalBox.IsChecked = false;
            BackupApprovalBox.IsChecked = false;
            if (result.Status == PackageRemovalExecutionStatus.ValidationNoOp)
            {
                SetStatus(
                    $"Uninstall interaction validation complete. Removal permitted no. " +
                    $"PackageManager invocations {result.PackageManagerInvocations}. " +
                    $"Removal completed no. Application close expected no. " +
                    $"Installer processes {result.InstallerProcessesStarted}. " +
                    $"Elevation requests {(result.RequestsElevation ? 1 : 0)}. " +
                    $"Production actions {result.ProductionActions}.");
            }
            else
            {
                SetStatus(
                    "Windows reported terminal current-user package removal. DCx may close immediately. " +
                    $"PackageManager invocations {result.PackageManagerInvocations}. " +
                    $"Removal completed {(result.RemovalCompleted ? "yes" : "no")}. " +
                    $"Production actions {result.ProductionActions}.");
            }

            StatusText.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (PackageRemovalWorkflowException exception)
        {
            completed = true;
            ConsequencesApprovalBox.IsChecked = false;
            BackupApprovalBox.IsChecked = false;
            SetStatus(exception.Code switch
            {
                "package_removal_review_expired" or "package_removal_review_changed" =>
                    "Uninstall was denied because the exact package review expired or changed. Close and reopen this review. No retry is available here.",
                "package_removal_request_consumed" =>
                    "This one-use uninstall request was already consumed. Close and reopen this review.",
                _ =>
                    "Uninstall did not return a trusted terminal result. The one-use request is closed. Verify package state before any new attempt.",
            });
            StatusText.Focus();
        }
        catch
        {
            completed = true;
            ConsequencesApprovalBox.IsChecked = false;
            BackupApprovalBox.IsChecked = false;
            SetStatus(
                "Uninstall failed closed without exposing platform details. The one-use request is closed. Verify package state before any new attempt.");
            StatusText.Focus();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string FormatReview(PackageRemovalReview review) => string.Join(
        Environment.NewLine,
        $"Requested operation: {PackageRemovalReviewPolicy.OperationKind}",
        $"Typed Windows API: {PackageRemovalReviewPolicy.DeploymentApi}",
        $"Removal options: {PackageRemovalReviewPolicy.RemovalOptions}",
        $"Removal scope: {PackageRemovalReviewPolicy.Scope}",
        "Requests elevation: no",
        $"Package name: {review.PackageName}",
        $"Package full name: {review.PackageFullName}",
        $"Publisher: {review.Publisher}",
        $"Publisher ID: {review.PublisherId}",
        $"Version: {review.Version}",
        $"Architecture: {review.Architecture}",
        $"Exact review SHA-256: {review.ReviewSha256}",
        $"Review expires UTC: {review.ExpiresUtc:O}",
        "Windows may close DCx: yes",
        "Cancellation after API entry: no",
        "Package application data preserved: no",
        "Unused dependencies may be removed: yes",
        "The separate DCx backup includes approved browser metadata and private knowledge only. It excludes sessions, cookies, credentials, global replicas, and protected settings.");

    private void SetBusy(bool value)
    {
        busy = value;
        ConsequencesApprovalBox.IsEnabled = !value && !completed &&
            workflow.Availability == PackageRemovalAvailability.Available;
        BackupApprovalBox.IsEnabled = !value && !completed &&
            workflow.Availability == PackageRemovalAvailability.Available;
        ExecuteButton.IsEnabled = !value && !completed &&
            workflow.Availability == PackageRemovalAvailability.Available;
        CloseButton.IsEnabled = !value;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            SetStatus(
                "This window cannot close while the one-use operation is active. Windows package removal cannot be canceled after API entry.");
            StatusText.Focus();
            return;
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }
}
