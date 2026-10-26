using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using DCx.Core.Deployment;
using DCx.PrerequisiteBootstrapper.Core;
using CorePrerequisiteBootstrapPolicy = DCx.Core.Deployment.PrerequisiteBootstrapPolicy;

namespace DCx.PrerequisiteBootstrapper;

/// <summary>
/// Native, one-item-at-a-time review UI. This class intentionally contains no Process, browser,
/// ShellExecute, registry, network, package-manager, PowerShell, elevation, or reboot call.
/// </summary>
public partial class BootstrapperWindow : Window
{
    private readonly BootstrapperComposition composition;
    private readonly PrerequisiteBootstrapperSession session;
    private PrerequisiteBootstrapPrerequisite? prerequisite;
    private PrerequisiteDetectionEvidence? detection;
    private SelectedVendorArtifact? selectedArtifact;
    private VendorLaunchPlan? plan;
    private readonly DispatcherTimer policyFreshnessTimer;
    private bool updating;

    internal BootstrapperWindow(BootstrapperComposition composition)
    {
        this.composition = composition ?? throw new ArgumentNullException(nameof(composition));
        var nowUtc = DateTimeOffset.UtcNow;
        session = new PrerequisiteBootstrapperSession(
            Guid.NewGuid().ToString("N"),
            nowUtc,
            nowUtc.Add(PrerequisiteBootstrapperPolicy.MaximumSessionLifetime));

        InitializeComponent();
        policyFreshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        policyFreshnessTimer.Tick += (_, _) => EvaluatePlan();
        policyFreshnessTimer.Start();
        Closed += (_, _) => policyFreshnessTimer.Stop();
        PrerequisiteBox.ItemsSource = CorePrerequisiteBootstrapPolicy.RequiredPrerequisites;
        PrerequisiteBox.DisplayMemberPath = nameof(PrerequisiteBootstrapPrerequisite.DisplayName);
        PrerequisiteBox.SelectedIndex = 0;
        Loaded += (_, _) => HeadingText.Focus();
    }

    private void OnPrerequisiteChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (updating || PrerequisiteBox.SelectedItem is not PrerequisiteBootstrapPrerequisite selected)
        {
            return;
        }

        prerequisite = selected;
        ResetItemReview();
        RefreshEvidence();
        TermsAcknowledgementBox.Focus();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ResetItemReview();
        RefreshEvidence();
        SetStatus(composition.IsFixture
            ? "Fixture evidence refreshed: fixed synthetic Missing status. No detector, browser, process, UAC, network, PowerShell, winget, reboot, or installer ran."
            : "Detection remains unavailable: normal composition has no real fresh detector. No probe or process was run.");
    }

    private void OnViewTermsClick(object sender, RoutedEventArgs e)
    {
        if (prerequisite is null)
        {
            return;
        }

        SetStatus($"Official terms URI for {prerequisite.DisplayName}: {prerequisite.LicenseUri.AbsoluteUri} No browser was opened. Reviewing this URI is not acceptance of vendor or Microsoft terms.");
    }

    private void OnSelectArtifactClick(object sender, RoutedEventArgs e)
    {
        if (prerequisite is null || prerequisite.Id == "windows-wsl2")
        {
            selectedArtifact = null;
            EvaluatePlan();
            SetStatus("Artifact selection applies only to WebView2 Runtime or Docker Desktop. WSL uses its fixed command and no artifact.");
            return;
        }

        selectedArtifact = composition.TrySelectPrevalidatedFixtureArtifact(session, prerequisite, DateTimeOffset.UtcNow);
        if (selectedArtifact is null)
        {
            EvaluatePlan();
            SetStatus("Artifact selection is refused in normal composition until a real selected-artifact verifier is implemented. No file picker, filesystem probe, hash, signature check, or process ran.");
            return;
        }

        EvaluatePlan();
        SetStatus($"Fixture selected a prevalidated synthetic artifact record for {prerequisite.DisplayName}. It is not a real file and no verifier, browser, process, UAC, network, PowerShell, winget, reboot, or installer ran.");
    }

    private void OnConsentChanged(object sender, RoutedEventArgs e)
    {
        if (!updating)
        {
            EvaluatePlan();
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        // Reevaluate synchronously so a control cannot use evidence that has just become stale.
        EvaluatePlan();
        if (plan is not { Permitted: true })
        {
            SetStatus("Start remains unavailable because the data-only policy did not permit an exact plan. No process was started.");
            return;
        }

        composition.Recorder.Record("permitted_plan_reviewed_without_launch");
        SetStatus("The policy plan is permitted, but real launch is unavailable pending a separately reviewed verifier and launcher. No process, browser, UAC prompt, download, package manager, PowerShell, winget, reboot, or installer was started. Vendor/Microsoft remains responsible for its own terms and elevation.");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void RefreshEvidence()
    {
        if (prerequisite is null)
        {
            return;
        }

        detection = composition.CreateDetectionEvidence(session, prerequisite.Id, DateTimeOffset.UtcNow);
        OfficialUriText.Text = prerequisite.OfficialUri.AbsoluteUri;
        TermsUriText.Text = prerequisite.LicenseUri.AbsoluteUri;
        DetectionStatusText.Text = detection.Status.ToString();
        DetectionTimestampText.Text = detection.ObservedAtUtc.ToUniversalTime().ToString("O");
        PlannedCommandText.Text = "wsl.exe --install --no-distribution";
        SelectArtifactButton.IsEnabled = prerequisite.Id is "webview2-runtime" or "docker-desktop-windows";
        AutomationProperties.SetName(OfficialUriText, OfficialUriText.Text);
        AutomationProperties.SetName(TermsUriText, TermsUriText.Text);
        AutomationProperties.SetName(DetectionStatusText, $"Fresh detection status {DetectionStatusText.Text}");
        AutomationProperties.SetName(DetectionTimestampText, $"Fresh detection timestamp {DetectionTimestampText.Text}");
        EvaluatePlan();
    }

    private void ResetItemReview()
    {
        updating = true;
        try
        {
            selectedArtifact = null;
            TermsAcknowledgementBox.IsChecked = false;
            StartApprovalBox.IsChecked = false;
        }
        finally
        {
            updating = false;
        }
    }

    private void EvaluatePlan()
    {
        if (prerequisite is null || detection is null)
        {
            return;
        }

        var consent = PrerequisiteBootstrapperPolicy.CreateInitialConsent(session, prerequisite.Id) with
        {
            TermsAcknowledged = TermsAcknowledgementBox.IsChecked == true,
            LaunchApproved = StartApprovalBox.IsChecked == true,
        };
        var request = prerequisite.Id == "windows-wsl2"
            ? new VendorLaunchRequest(prerequisite.Id, VendorLaunchKind.WindowsSubsystemForLinuxInstall)
            : new VendorLaunchRequest(prerequisite.Id, VendorLaunchKind.SelectedArtifact, selectedArtifact);
        plan = PrerequisiteBootstrapperPolicy.BuildVendorLaunchPlan(
            session,
            detection,
            consent,
            request,
            DateTimeOffset.UtcNow);
        StartButton.IsEnabled = plan.Permitted;
        PlanText.Text = FormatPlan(plan);
        AutomationProperties.SetName(PlanText, PlanText.Text);
    }

    private static string FormatPlan(VendorLaunchPlan currentPlan) =>
        currentPlan.Permitted
            ? $"Permitted data-only plan: {currentPlan.Executable} {string.Join(" ", currentPlan.Arguments)}. Process execution authorized: no. Real launcher: unavailable pending verifier/launcher."
            : $"Not permitted: {currentPlan.Code}. Process execution authorized: no. Real launcher: unavailable pending verifier/launcher.";

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        AutomationProperties.SetName(StatusText, message);
        StatusText.Dispatcher.BeginInvoke(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(StatusText) ??
                       UIElementAutomationPeer.CreatePeerForElement(StatusText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }
}
