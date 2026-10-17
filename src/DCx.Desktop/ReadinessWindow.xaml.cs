using System.Windows;
using System.Windows.Automation;
using DCx.Core.Release;
using DCx.Core.Storage;

namespace DCx.Desktop;

public partial class ReadinessWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();

    public ReadinessWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
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

            await using var store = await PilotMetricAggregateStore.OpenAsync(
                ApplicationDataRoot.Resolve(),
                lifetime.Token);
            var consent = await store.GetConsentAsync(lifetime.Token);
            SetConsent(consent.Enabled
                ? $"Local aggregate metric recording is opted in under notice version {consent.NoticeVersion}. No network transport is implemented."
                : "Local aggregate metric recording is disabled. No pilot event collection or network transport is active.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetDecision($"Decision NO GO. Readiness data could not be fully loaded. {exception.Message}");
            SetConsent("Metric recording remains unavailable.");
            SetSummary("Readiness counts are unavailable.");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
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
