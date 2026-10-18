using System.Windows;
using System.Windows.Automation;
using DCx.Core.Release;
using DCx.Core.Storage;

namespace DCx.Desktop;

public partial class ReadinessWindow : Window
{
    private const int MetricNoticeVersion = 1;
    private readonly CancellationTokenSource lifetime = new();
    private bool actionInProgress;
    private bool closed;
    private bool metricRecordingEnabled;

    public ReadinessWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            if (!closed) Heading.Focus();
        };
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnMetricApprovalChanged(object sender, RoutedEventArgs e) =>
        UpdateActionAvailability();

    private async void OnEnableMetricRecordingClick(object sender, RoutedEventArgs e)
    {
        if (actionInProgress || metricRecordingEnabled || MetricOptInApprovalBox.IsChecked != true)
        {
            SetActionStatus("No setting changed. Review and check the local aggregate recording consent first.", true);
            MetricOptInApprovalBox.Focus();
            return;
        }

        await ChangeConsentAsync(
            enabled: true,
            "Local aggregate recording is enabled under notice version 1. No product event source or network transport is connected.");
    }

    private async void OnStopAndPurgeMetricsClick(object sender, RoutedEventArgs e)
    {
        if (actionInProgress || !metricRecordingEnabled || MetricOptOutApprovalBox.IsChecked != true)
        {
            SetActionStatus("No setting changed. Review and check the stop-and-purge approval first.", true);
            MetricOptOutApprovalBox.Focus();
            return;
        }

        await ChangeConsentAsync(
            enabled: false,
            "Local aggregate recording stopped. All retained local aggregate metrics were permanently deleted. No network request occurred.");
    }

    private async Task RefreshAsync()
    {
        if (actionInProgress) return;
        actionInProgress = true;
        UpdateActionAvailability();
        try
        {
            var result = CurrentReleaseReadiness.EvaluateLocalNoGo();
            BlockersList.ItemsSource = result.Issues
                .Where(issue => issue.Severity is ReadinessSeverity.Critical or ReadinessSeverity.Major &&
                                issue.Disposition != ReadinessDisposition.Validated)
                .Select(issue =>
                    $"{issue.Severity}: {issue.Title}. Owner: {issue.OwnerRole}. Next: {issue.NextAction} Evidence: {issue.EvidenceReference}")
                .ToArray();
            MetricsList.ItemsSource = result.PilotMetrics
                .Select(metric => $"{metric.Key}. {metric.Unit}. {metric.Aggregation}. {metric.PrivacyRule}")
                .ToArray();
            SetDecision(
                $"Decision NO GO. Production activation is disabled. " +
                $"{result.BlockingCriticalAndMajorIssues} blocking critical or major issues remain.");
            SetSummary(
                $"{result.BlockingCriticalAndMajorIssues} blockers and {result.PilotMetrics.Count} privacy-reviewed metric definitions are listed.");

            await RefreshConsentAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetDecision("Decision NO GO. Readiness data could not be fully loaded.");
            SetConsent("Metric recording remains unavailable.");
            SetSummary("Readiness counts are unavailable.");
        }
        finally
        {
            actionInProgress = false;
            if (!closed)
            {
                UpdateActionAvailability();
            }
        }
    }

    private async Task ChangeConsentAsync(bool enabled, string completedStatus)
    {
        actionInProgress = true;
        UpdateActionAvailability();
        SetActionStatus(enabled
            ? "Enabling local aggregate recording."
            : "Stopping local aggregate recording and deleting retained aggregates.");
        try
        {
            await using var store = await PilotMetricAggregateStore.OpenAsync(
                ApplicationDataRoot.Resolve(),
                lifetime.Token);
            await store.SetConsentAsync(
                enabled,
                MetricNoticeVersion,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            metricRecordingEnabled = enabled;
            MetricOptInApprovalBox.IsChecked = false;
            MetricOptOutApprovalBox.IsChecked = false;
            await RefreshConsentAsync();
            SetActionStatus(completedStatus, true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetActionStatus(
                "The local metric privacy setting could not be changed. No production or network action occurred.",
                true);
            await TryRefreshConsentAsync();
        }
        finally
        {
            actionInProgress = false;
            if (!closed) UpdateActionAvailability();
        }
    }

    private async Task RefreshConsentAsync()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(
            ApplicationDataRoot.Resolve(),
            lifetime.Token);
        var consent = await store.GetConsentAsync(lifetime.Token);
        var aggregateCount = await store.GetAggregateRowCountAsync(lifetime.Token);
        metricRecordingEnabled = consent.Enabled;
        SetConsent(consent.Enabled
            ? $"Local aggregate metric recording is opted in under notice version {consent.NoticeVersion}. " +
              $"{aggregateCount} daily aggregate buckets are stored. No product event source or network transport is connected."
            : "Local aggregate metric recording is disabled. 0 daily aggregate buckets are stored. " +
              "No pilot event collection or network transport is active.");
    }

    private async Task TryRefreshConsentAsync()
    {
        try
        {
            await RefreshConsentAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            metricRecordingEnabled = false;
            SetConsent("Metric recording remains unavailable.");
        }
    }

    private void UpdateActionAvailability()
    {
        if (closed) return;
        EnableMetricRecordingButton.IsEnabled =
            !actionInProgress && !metricRecordingEnabled && MetricOptInApprovalBox.IsChecked == true;
        StopAndPurgeMetricsButton.IsEnabled =
            !actionInProgress && metricRecordingEnabled && MetricOptOutApprovalBox.IsChecked == true;
        MetricOptInApprovalBox.IsEnabled = !actionInProgress && !metricRecordingEnabled;
        MetricOptOutApprovalBox.IsEnabled = !actionInProgress && metricRecordingEnabled;
        RefreshButton.IsEnabled = !actionInProgress;
    }

    private void SetActionStatus(string value, bool moveFocus = false)
    {
        ConsentActionStatus.Text = value;
        AutomationProperties.SetName(ConsentActionStatus, value);
        if (moveFocus && !closed) ConsentActionStatus.Focus();
    }

    private void SetDecision(string value)
    {
        DecisionText.Text = value;
        AutomationProperties.SetName(DecisionText, value);
    }

    private void SetConsent(string value)
    {
        ConsentText.Text = value;
        AutomationProperties.SetName(ConsentText, value);
    }

    private void SetSummary(string value)
    {
        SummaryText.Text = value;
        AutomationProperties.SetName(SummaryText, value);
    }
}
