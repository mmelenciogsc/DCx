using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using DCx.Core.Deployment;

namespace DCx.Desktop;

/// <summary>
/// Displays one pre-detected prerequisite at a time and records only local review decisions.
/// This window has no download, installer, elevation, PowerShell, or navigation authority.
/// </summary>
public partial class PrerequisiteReviewWindow : Window
{
    private readonly IReadOnlyList<PrerequisiteBootstrapItem> items;
    private PrerequisiteBootstrapConsent? consent;
    private PrerequisiteBootstrapDecision? decision;
    private int currentIndex;
    private bool updating;

    public PrerequisiteReviewWindow(IEnumerable<PrerequisiteBootstrapItem> reviewItems)
    {
        ArgumentNullException.ThrowIfNull(reviewItems);
        items = reviewItems.ToArray();
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one prerequisite review item is required.", nameof(reviewItems));
        }

        InitializeComponent();
        Loaded += (_, _) => LoadCurrentItem(focusHeading: true);
    }

    public IReadOnlyList<PrerequisiteBootstrapDecision> RecordedDecisions { get; private set; } = [];

    private PrerequisiteBootstrapItem CurrentItem => items[currentIndex];

    private void LoadCurrentItem(bool focusHeading)
    {
        updating = true;
        try
        {
            consent = null;
            TermsAcknowledgementBox.IsChecked = false;
            InstallApprovalBox.IsChecked = false;
            PositionText.Text = $"Prerequisite {currentIndex + 1} of {items.Count}";
            var prerequisite = CurrentItem.Prerequisite;
            PrerequisiteNameText.Text = prerequisite.DisplayName;
            PublisherText.Text = prerequisite.Publisher;
            OfficialUriText.Text = prerequisite.OfficialUri.AbsoluteUri;
            TermsUriText.Text = prerequisite.LicenseUri.AbsoluteUri;
            PrivilegeText.Text = prerequisite.RequiresElevation
                ? "An external vendor installation may request administrator elevation. This review window cannot request elevation."
                : "The reviewed prerequisite does not require elevation. This review window cannot request elevation.";
            DetailsText.Text = FormatDetails(CurrentItem);
            SetAutomationNames();
        }
        finally
        {
            updating = false;
        }

        EvaluateCurrentItem();
        if (focusHeading)
        {
            HeadingText.Focus();
        }
    }

    private void OnConsentChanged(object sender, RoutedEventArgs e)
    {
        if (updating) return;
        consent = new PrerequisiteBootstrapConsent(
            CurrentItem.Prerequisite.Id,
            TermsAcknowledgementBox.IsChecked == true,
            InstallApprovalBox.IsChecked == true,
            Declined: false);
        EvaluateCurrentItem();
    }

    private void OnDeclineClick(object sender, RoutedEventArgs e)
    {
        consent = new PrerequisiteBootstrapConsent(
            CurrentItem.Prerequisite.Id,
            TermsAcknowledged: false,
            InstallApproved: false,
            Declined: true);
        updating = true;
        try
        {
            TermsAcknowledgementBox.IsChecked = false;
            InstallApprovalBox.IsChecked = false;
        }
        finally
        {
            updating = false;
        }
        EvaluateCurrentItem();
        ContinueButton.Focus();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        LoadCurrentItem(focusHeading: false);
        TermsAcknowledgementBox.Focus();
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (decision is null || !CanContinue(decision))
        {
            SetStatus("Continue is unavailable until this prerequisite is ready, declined, or has both separate consent checkboxes selected. No installation was started.");
            TermsAcknowledgementBox.Focus();
            return;
        }

        RecordedDecisions = [.. RecordedDecisions, decision];
        if (currentIndex + 1 < items.Count)
        {
            currentIndex++;
            LoadCurrentItem(focusHeading: true);
            return;
        }

        SetStatus("Prerequisite review is complete. Decisions were recorded for review only. Downloads 0. Installers started 0. Elevation requests 0. PowerShell invocations 0.");
        ContinueButton.IsEnabled = false;
        DeclineButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        StatusText.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void EvaluateCurrentItem()
    {
        decision = PrerequisiteBootstrapPolicy.Evaluate(CurrentItem, consent);
        var ready = decision.State == PrerequisiteBootstrapDecisionState.Ready;
        var blocked = decision.State == PrerequisiteBootstrapDecisionState.Blocked;
        TermsAcknowledgementBox.IsEnabled = !ready && !blocked && decision.State != PrerequisiteBootstrapDecisionState.Declined;
        InstallApprovalBox.IsEnabled = !ready && !blocked && decision.State != PrerequisiteBootstrapDecisionState.Declined;
        DeclineButton.IsEnabled = !ready && !blocked && decision.State != PrerequisiteBootstrapDecisionState.Declined;
        RetryButton.IsEnabled = true;
        ContinueButton.IsEnabled = CanContinue(decision);
        SetStatus($"{decision.StatusText} {decision.HelpText}");
    }

    private static bool CanContinue(PrerequisiteBootstrapDecision currentDecision) =>
        currentDecision.State is PrerequisiteBootstrapDecisionState.Ready or
            PrerequisiteBootstrapDecisionState.Declined or
            PrerequisiteBootstrapDecisionState.Missing;

    private void SetAutomationNames()
    {
        AutomationProperties.SetName(PrerequisiteNameText, PrerequisiteNameText.Text);
        AutomationProperties.SetName(PublisherText, PublisherText.Text);
        AutomationProperties.SetName(OfficialUriText, OfficialUriText.Text);
        AutomationProperties.SetName(TermsUriText, TermsUriText.Text);
        AutomationProperties.SetName(PrivilegeText, PrivilegeText.Text);
        AutomationProperties.SetName(DetailsText, DetailsText.Text);
        AutomationProperties.SetName(PositionText, PositionText.Text);
    }

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

    private static string FormatDetails(PrerequisiteBootstrapItem item) => string.Join(
        Environment.NewLine,
        $"Identifier: {item.Prerequisite.Id}",
        $"Detection result: {item.DetectionStatus}",
        $"Terms text digest reference: {item.Prerequisite.LicenseTextDigestReference}",
        $"External elevation may be requested: {(item.Prerequisite.RequiresElevation ? "yes" : "no")}",
        "DCx review authority: decision recording only",
        "Downloads authorized: no",
        "Installer execution authorized: no",
        "Elevation authorized: no",
        "PowerShell invocation authorized: no");
}
