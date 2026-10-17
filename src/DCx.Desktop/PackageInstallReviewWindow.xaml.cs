using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using DCx.Core.Deployment;

namespace DCx.Desktop;

public partial class PackageInstallReviewWindow : Window
{
    private readonly PackageInstallReviewSession session;
    private bool sessionDisposed;

    public PackageInstallReviewWindow(PackageInstallReviewSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
        RequestDetailsText.Text = FormatProposal(session.Proposal);
        Loaded += (_, _) => ApprovalCheckBox.Focus();
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        SetPendingControlsEnabled(false);
        SetStatus("Revalidating the exact package digest and trust evidence. No installer has started.");
        try
        {
            var receipt = await session.ApproveForValidationAsync(
                ApprovalCheckBox.IsChecked == true,
                DateTimeOffset.UtcNow);
            ApprovalCheckBox.IsChecked = false;
            ApprovalCheckBox.IsEnabled = false;
            SetStatus(receipt.ExecutionPermitted || receipt.InstallerProcessesStarted != 0
                ? "The validation boundary returned an unsafe result. Treat this request as denied."
                : "Approval recorded for this exact package. Validation mode did not permit execution or start an installer.");
        }
        catch (PackageInstallReviewException exception)
        {
            if (exception.Code == "package_review_approval_required")
            {
                SetPendingControlsEnabled(true);
                ApprovalCheckBox.Focus();
                SetStatus("Approval was not recorded. Check the exact-request approval box first. No installer was started.");
                return;
            }

            ApprovalCheckBox.IsChecked = false;
            ApprovalCheckBox.IsEnabled = false;
            SetStatus(exception.Code switch
            {
                "package_review_expired" =>
                    "Request denied because the review expired. No installer was started.",
                "package_review_package_changed" or "package_review_trust_invalid" or
                    "package_review_trust_unavailable" =>
                    "Request denied because the exact package or its trust evidence did not revalidate. No installer was started.",
                _ => "Request denied by the package review boundary. No installer was started.",
            });
        }
    }

    private async void OnCancelRequestClick(object sender, RoutedEventArgs e)
    {
        SetPendingControlsEnabled(false);
        try
        {
            await session.CancelAsync();
            ApprovalCheckBox.IsChecked = false;
            ApprovalCheckBox.IsEnabled = false;
            SetStatus("Installation request cancelled. No installer was started.");
        }
        catch (PackageInstallReviewException)
        {
            SetStatus("The request was already completed or denied. No installer was started.");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (session.State == PackageInstallReviewState.Pending)
        {
            CancelRequestButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        else
        {
            Close();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sessionDisposed) return;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        sessionDisposed = true;
    }

    private void SetPendingControlsEnabled(bool enabled)
    {
        ApprovalCheckBox.IsEnabled = enabled;
        ApproveButton.IsEnabled = enabled;
        CancelRequestButton.IsEnabled = enabled;
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        AutomationProperties.SetName(StatusText, status);
    }

    private static string FormatProposal(PackageInstallProposal proposal) => string.Join(
        Environment.NewLine,
        $"Requested operation: {proposal.OperationKind}",
        $"Typed Windows API: {proposal.DeploymentApi}",
        $"Deployment options: {proposal.DeploymentOptions}",
        $"Installation scope: {proposal.Scope}",
        $"Requests elevation: {(proposal.RequestsElevation ? "yes" : "no")}",
        $"Package file: {proposal.PackageFileName}",
        $"Local package path: {proposal.PackagePath}",
        $"Package URI: {proposal.PackageUri}",
        $"Package identity: {proposal.PackageIdentityName}",
        $"Publisher: {proposal.PublisherId}",
        $"Version: {proposal.Version}",
        $"Size in bytes: {proposal.PackageLength.ToString(CultureInfo.InvariantCulture)}",
        $"Package SHA-256: {proposal.PackageSha256}",
        $"Proposal SHA-256: {proposal.ProposalSha256}",
        $"Review expires UTC: {proposal.ExpiresUtc:O}");
}
