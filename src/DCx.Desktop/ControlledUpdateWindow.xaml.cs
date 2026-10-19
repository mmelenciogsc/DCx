using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using DCx.Core.Reliability;

namespace DCx.Desktop;

public enum ControlledUpdateSourceKind
{
    Unavailable,
    GeneratedFixture,
}

public partial class ControlledUpdateWindow : Window
{
    private readonly IControlledUpdateReviewSource source;
    private readonly ControlledUpdateSourceKind sourceKind;
    private readonly CancellationTokenSource lifetime = new();
    private ControlledUpdateReview? reviewedUpdate;
    private ControlledUpdateReview? stagedUpdate;
    private ControlledUpdateReview? activationReadyReview;
    private ControlledUpdateActivationReadiness? activationReadiness;
    private bool closed;

    public ControlledUpdateWindow(
        IControlledUpdateReviewSource source,
        ControlledUpdateSourceKind sourceKind = ControlledUpdateSourceKind.Unavailable)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.sourceKind = sourceKind;
        InitializeComponent();
        BoundaryText.Text = sourceKind == ControlledUpdateSourceKind.GeneratedFixture
            ? "Deterministic local validation fixture. The manifest and package are synthetic in-memory inputs. Network requests, installer processes, elevation requests, activation attempts, restarts, and production actions are unavailable."
            : "Production update service is not configured. No update endpoint, signing root, package download, installer, elevation, activation, restart, or production action is available.";
        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        SetStatus(sourceKind == ControlledUpdateSourceKind.GeneratedFixture
            ? "Ready to review the local synthetic update. Network requests 0. Package requests 0."
            : "Controlled updates are unavailable until a production update origin and signing root are provisioned and approved. Network requests 0.");
        Loaded += (_, _) => HeadingText.Focus();
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e) =>
        await CheckAsync();

    private async Task CheckAsync()
    {
        reviewedUpdate = null;
        stagedUpdate = null;
        ApprovalBox.IsChecked = false;
        ApprovalBox.IsEnabled = false;
        StageButton.IsEnabled = false;
        ActivationReviewApprovalBox.IsChecked = false;
        ActivationReviewApprovalBox.IsEnabled = false;
        ActivationReviewButton.IsEnabled = false;
        ResetActivationValidation();
        CheckButton.IsEnabled = false;
        SetStatus("Checking the configured controlled-update source. No package will be staged by this action.");
        try
        {
            var snapshot = await source.CheckAsync(lifetime.Token);
            ControlledUpdateReviewSourcePolicy.RequireSnapshot(snapshot);
            if (closed) return;
            CurrentVersionText.Text = $"Current version: {snapshot.CurrentVersion}.";
            AutomationProperties.SetName(CurrentVersionText, CurrentVersionText.Text);
            if (snapshot.StatusCode == ControlledUpdateReviewSourcePolicy.NotConfigured)
            {
                SetReview("No controlled-update source is configured. No update state is inferred.");
                SetStatus("Controlled updates remain unavailable. Manifest requests 0. Package requests 0. Network requests 0. Installer processes 0.");
                ReviewDetails.Focus();
                return;
            }

            reviewedUpdate = snapshot.Review;
            SetReview(FormatReview(reviewedUpdate!));
            ApprovalBox.IsEnabled = true;
            StageButton.IsEnabled = true;
            SetStatus(
                $"Exact signed update review ready. Manifest requests {snapshot.ManifestRequests}. " +
                $"Package requests {snapshot.PackageRequests}. Network requests {snapshot.NetworkRequests}. " +
                "Nothing was staged or installed.");
            ReviewDetails.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetReview("The controlled update could not be reviewed safely. No package was staged.");
            SetStatus("Update review failed closed. No installation, elevation, activation, or restart occurred.");
            ReviewDetails.Focus();
        }
        finally
        {
            if (!closed) CheckButton.IsEnabled = true;
        }
    }

    private async void OnStageClick(object sender, RoutedEventArgs e)
    {
        if (reviewedUpdate is null)
        {
            SetStatus("Staging denied. Check and review an exact signed update first.");
            CheckButton.Focus();
            return;
        }

        if (ApprovalBox.IsChecked != true)
        {
            SetStatus("Staging denied. Review the exact update and check the separate staging approval first.");
            ApprovalBox.Focus();
            return;
        }

        var exactReview = reviewedUpdate;
        CheckButton.IsEnabled = false;
        StageButton.IsEnabled = false;
        ApprovalBox.IsEnabled = false;
        SetStatus("Revalidating the signed manifest, package bytes, identity, publisher, version, and digest before inert staging.");
        try
        {
            var result = await source.StageAsync(
                exactReview,
                explicitlyApproved: true,
                lifetime.Token);
            ControlledUpdateReviewSourcePolicy.RequireResult(exactReview, result);
            if (closed) return;
            ApprovalBox.IsChecked = false;
            reviewedUpdate = null;
            stagedUpdate = exactReview;
            ActivationReviewApprovalBox.IsChecked = false;
            ActivationReviewApprovalBox.IsEnabled = true;
            ActivationReviewButton.IsEnabled = true;
            ResetActivationValidation();
            SetReview(
                FormatReview(exactReview) + Environment.NewLine + Environment.NewLine +
                $"Staged inertly after exact revalidation. Manifest requests {result.ManifestRequests}. " +
                $"Package requests {result.PackageRequests}. Network requests {result.NetworkRequests}. " +
                "Execution permitted no. " +
                $"Installer processes {result.InstallerProcessesStarted}. Elevation requests {result.ElevationRequests}. " +
                $"Activation attempts {result.ActivationAttempts}. Restart attempts {result.RestartAttempts}. " +
                $"Production actions {result.ProductionActions}.");
            SetStatus(
                $"Version {result.Version} was staged inertly. Execution permitted no. " +
                $"Installer processes {result.InstallerProcessesStarted}. Elevation requests {result.ElevationRequests}. " +
                $"Activation attempts {result.ActivationAttempts}. Restart attempts {result.RestartAttempts}. " +
                $"Production actions {result.ProductionActions}.");
            ReviewDetails.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ApprovalBox.IsChecked = false;
            reviewedUpdate = null;
            stagedUpdate = null;
            ActivationReviewApprovalBox.IsChecked = false;
            ActivationReviewApprovalBox.IsEnabled = false;
            ActivationReviewButton.IsEnabled = false;
            ResetActivationValidation();
            SetReview("Staging failed closed. Review the update again before retrying.");
            SetStatus("No package was admitted. No installation, elevation, activation, restart, or production action occurred.");
            ReviewDetails.Focus();
        }
        finally
        {
            if (!closed) CheckButton.IsEnabled = true;
        }
    }

    private async void OnActivationReviewClick(object sender, RoutedEventArgs e)
    {
        if (stagedUpdate is null)
        {
            SetStatus("Final integrity review denied. Stage and revalidate an exact update first.");
            CheckButton.Focus();
            return;
        }

        if (ActivationReviewApprovalBox.IsChecked != true)
        {
            SetStatus("Final integrity review denied. Check the separate review request first.");
            ActivationReviewApprovalBox.Focus();
            return;
        }

        var exactReview = stagedUpdate;
        var stagedDetails = ReviewDetails.Text;
        CheckButton.IsEnabled = false;
        ActivationReviewApprovalBox.IsEnabled = false;
        ActivationReviewButton.IsEnabled = false;
        SetStatus("Reopening the exact staged set for a fresh, read-only integrity and trust review. No activation is permitted.");
        try
        {
            var result = await source.PrepareActivationReadinessAsync(
                exactReview,
                explicitlyApproved: true,
                lifetime.Token);
            ControlledUpdateReviewSourcePolicy.RequireActivationReadinessResult(
                exactReview,
                result);
            if (closed) return;
            var readiness = result.Readiness;
            activationReadyReview = exactReview;
            activationReadiness = readiness;
            ActivationValidationApprovalBox.IsChecked = false;
            ActivationValidationApprovalBox.IsEnabled = true;
            ActivationValidationButton.IsEnabled = true;
            SetReview(
                stagedDetails + Environment.NewLine + Environment.NewLine +
                $"Final staged-set integrity review complete at {readiness.RevalidatedUtc:O}.{Environment.NewLine}" +
                $"Activation-readiness SHA-256: {readiness.ReadinessSha256}.{Environment.NewLine}" +
                "Execution permitted no. " +
                $"Manifest requests {result.ManifestRequests}. Package requests {result.PackageRequests}. " +
                $"Network requests {result.NetworkRequests}. Installer processes {result.InstallerProcessesStarted}. " +
                $"Elevation requests {result.ElevationRequests}. Activation attempts {result.ActivationAttempts}. " +
                $"Restart attempts {result.RestartAttempts}. Production actions {result.ProductionActions}.");
            SetStatus(
                $"Version {readiness.Version} passed a fresh staged-set integrity review. Execution permitted no. " +
                $"Network requests {result.NetworkRequests}. Installer processes {result.InstallerProcessesStarted}. " +
                $"Elevation requests {result.ElevationRequests}. Activation attempts {result.ActivationAttempts}. " +
                $"Restart attempts {result.RestartAttempts}. Production actions {result.ProductionActions}.");
            ReviewDetails.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ResetActivationValidation();
            SetReview("Final staged-set integrity review failed closed. Check and stage the update again before retrying.");
            SetStatus("No activation readiness was admitted. No installation, elevation, activation, restart, or production action occurred.");
            ReviewDetails.Focus();
        }
        finally
        {
            stagedUpdate = null;
            ActivationReviewApprovalBox.IsChecked = false;
            ActivationReviewApprovalBox.IsEnabled = false;
            ActivationReviewButton.IsEnabled = false;
            if (!closed) CheckButton.IsEnabled = true;
        }
    }

    private async void OnActivationValidationClick(object sender, RoutedEventArgs e)
    {
        if (activationReadyReview is null || activationReadiness is null)
        {
            SetStatus("Activation transaction validation denied. Complete the exact staged-set integrity review first.");
            CheckButton.Focus();
            return;
        }

        if (ActivationValidationApprovalBox.IsChecked != true)
        {
            SetStatus("Activation transaction validation denied. Check the separate synthetic validation approval first.");
            ActivationValidationApprovalBox.Focus();
            return;
        }

        var exactReview = activationReadyReview;
        var exactReadiness = activationReadiness;
        var reviewedDetails = ReviewDetails.Text;
        CheckButton.IsEnabled = false;
        ActivationValidationApprovalBox.IsEnabled = false;
        ActivationValidationButton.IsEnabled = false;
        SetStatus("Holding and revalidating the exact staged files around one synthetic validation no-op. PackageManager will not be invoked.");
        try
        {
            var result = await source.ValidateActivationTransactionAsync(
                exactReview,
                exactReadiness,
                explicitlyApproved: true,
                lifetime.Token);
            ControlledUpdateReviewSourcePolicy.RequireActivationValidationResult(
                exactReview,
                exactReadiness,
                result);
            if (closed) return;
            SetReview(
                reviewedDetails + Environment.NewLine + Environment.NewLine +
                "Lease-held activation transaction validation complete." + Environment.NewLine +
                $"Exact review SHA-256: {result.ReviewSha256}.{Environment.NewLine}" +
                $"Activation-readiness SHA-256: {result.ReadinessSha256}.{Environment.NewLine}" +
                $"Execution status: {result.ExecutionStatus}. " +
                $"PackageManager invocations {result.PackageManagerInvocations}. " +
                $"Registered {(result.IsRegistered ? "yes" : "no")}. " +
                $"Application close required {(result.ApplicationCloseRequired ? "yes" : "no")}. " +
                $"Application relaunch required {(result.ApplicationRelaunchRequired ? "yes" : "no")}. " +
                $"Restart required {(result.RestartRequired ? "yes" : "no")}. " +
                $"Application close requests {result.ApplicationCloseRequests}. " +
                $"Application relaunch attempts {result.ApplicationRelaunchAttempts}. " +
                $"Network requests {result.NetworkRequests}. Installer processes {result.InstallerProcessesStarted}. " +
                $"Elevation requests {result.ElevationRequests}. Activation attempts {result.ActivationAttempts}. " +
                $"Restart attempts {result.RestartAttempts}. Production actions {result.ProductionActions}.");
            SetStatus(
                $"Version {result.Version} completed lease-held activation transaction validation. " +
                $"PackageManager invocations {result.PackageManagerInvocations}. Registered no. " +
                "Application close required no. Application relaunch required no. Restart required no. " +
                $"Application close requests {result.ApplicationCloseRequests}. " +
                $"Application relaunch attempts {result.ApplicationRelaunchAttempts}. " +
                $"Installer processes {result.InstallerProcessesStarted}. Elevation requests {result.ElevationRequests}. " +
                $"Activation attempts {result.ActivationAttempts}. Restart attempts {result.RestartAttempts}. " +
                $"Production actions {result.ProductionActions}.");
            ReviewDetails.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetReview("Activation transaction validation failed closed. This one-use synthetic source cannot be retried; close and reopen the validation fixture.");
            SetStatus("No package manager call, registration, installation, elevation, activation, restart, or production action was admitted.");
            ReviewDetails.Focus();
        }
        finally
        {
            ResetActivationValidation();
            if (!closed) CheckButton.IsEnabled = true;
        }
    }

    private void ResetActivationValidation()
    {
        activationReadyReview = null;
        activationReadiness = null;
        ActivationValidationApprovalBox.IsChecked = false;
        ActivationValidationApprovalBox.IsEnabled = false;
        ActivationValidationButton.IsEnabled = false;
    }

    private static string FormatReview(ControlledUpdateReview review) =>
        $"Current version: {review.CurrentVersion}.{Environment.NewLine}" +
        $"Candidate version: {review.CandidateVersion}.{Environment.NewLine}" +
        $"Published UTC: {review.PublishedUtc:O}.{Environment.NewLine}" +
        $"Expires UTC: {review.ExpiresUtc:O}.{Environment.NewLine}" +
        $"Manifest address: {review.ManifestUri}.{Environment.NewLine}" +
        $"Package address: {review.PackageUri}.{Environment.NewLine}" +
        $"Package identity: {review.PackageIdentityName}.{Environment.NewLine}" +
        $"Publisher: {review.PublisherId}.{Environment.NewLine}" +
        $"Package bytes: {review.PackageLength.ToString(CultureInfo.InvariantCulture)}.{Environment.NewLine}" +
        $"Package SHA-256: {review.PackageSha256}.{Environment.NewLine}" +
        $"Signed-envelope SHA-256: {review.EnvelopeSha256}.{Environment.NewLine}" +
        $"Exact review SHA-256: {review.ReviewSha256}.{Environment.NewLine}" +
        "Approval authorizes inert staging only. It does not authorize installation, elevation, activation, restart, or execution.";

    private void SetReview(string value)
    {
        ReviewDetails.Text = value;
        AutomationProperties.SetName(ReviewDetails, value);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
