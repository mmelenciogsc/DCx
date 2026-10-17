using System.Windows;
using System.Windows.Automation;
using DCx.Core.Finance;

namespace DCx.Desktop;

public partial class CareOperationsWindow : Window
{
    private readonly ICareOperationsSource source;
    private readonly bool isFixture;
    private readonly CancellationTokenSource lifetime = new();
    private CareOperationsHealth? current;
    private bool closed;

    public CareOperationsWindow(ICareOperationsSource source, bool isFixture)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isFixture = isFixture;
        InitializeComponent();
        BoundaryText.Text = isFixture
            ? "Deterministic local validation fixture. Generated aggregate data only. Provider credentials, network requests, payments, payouts, subscriptions, and production actions are unavailable."
            : "The CARE operator connection is not configured. No queue state or provider availability is inferred. Processing is unavailable.";
        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Heading.Focus();
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetActionsEnabled(false);
        ProcessApprovalBox.IsChecked = false;
        try
        {
            var health = await source.RefreshAsync(lifetime.Token);
            CareOperationsPolicy.RequireHealth(health);
            if (closed) return;
            current = health;
            SetSummary(health);
            SetStatus(health.StatusCode switch
            {
                "not_configured" =>
                    "CARE operations remain unavailable. No queue state was inferred and no provider request occurred.",
                "operator_action_required" =>
                    "One or more records require operator review. Blocked records were not changed. No provider request occurred.",
                "retry_due" =>
                    "Due verified events are ready for one explicitly approved bounded local cycle. No provider request occurred.",
                "retry_scheduled" =>
                    "Verified events are waiting for their next local attempt. Nothing was processed and no provider request occurred.",
                _ =>
                    "No verified events are pending or blocked. No provider request occurred.",
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                current = null;
                SetSummaryUnavailable();
                SetStatus("CARE operations health failed closed. No queue state was inferred and no provider request occurred.");
            }
        }
        finally
        {
            if (!closed) SetActionsEnabled(true);
        }
    }

    private async void OnProcessClick(object sender, RoutedEventArgs e)
    {
        if (current is not { CanProcessDue: true } || ProcessApprovalBox.IsChecked != true)
        {
            ProcessApprovalBox.IsChecked = false;
            SetStatus("No records were processed. Review the available aggregate state and explicitly approve one local cycle first.");
            ProcessApprovalBox.Focus();
            return;
        }

        SetActionsEnabled(false);
        try
        {
            var result = await source.ProcessDueAsync(current, true, lifetime.Token);
            CareOperationsPolicy.RequireProcessResult(result);
            if (closed) return;
            current = result.Health;
            ProcessApprovalBox.IsChecked = false;
            SetSummary(result.Health);
            SetStatus(
                $"Bounded local cycle completed. Scanned {result.Scanned}. Completed {result.Completed}. " +
                $"Still pending {result.StillPending}. Blocked {result.Blocked}. Provider calls {result.ProviderCalls}.");
            SummaryText.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                ProcessApprovalBox.IsChecked = false;
                SetStatus("No records were processed. The local cycle failed closed and no provider request occurred.");
            }
        }
        finally
        {
            if (!closed) SetActionsEnabled(true);
        }
    }

    private void SetSummary(CareOperationsHealth health)
    {
        SummaryText.Text =
            $"Status: {health.StatusCode}.{Environment.NewLine}" +
            $"Pending: {health.Pending}.{Environment.NewLine}" +
            $"Due now: {health.Due}.{Environment.NewLine}" +
            $"Blocked: {health.Blocked}.{Environment.NewLine}" +
            $"Completed audit records: {health.Completed}.{Environment.NewLine}" +
            $"Maximum attempt count: {health.MaximumAttemptCount}.{Environment.NewLine}" +
            $"Oldest pending UTC: {FormatTime(health.OldestPendingUtc)}.{Environment.NewLine}" +
            $"Next attempt UTC: {FormatTime(health.NextAttemptUtc)}.{Environment.NewLine}" +
            $"Local processing available: {(health.CanProcessDue ? "yes" : "no")}.{Environment.NewLine}" +
            $"Data source: {(isFixture ? "deterministic local fixture" : "not configured")}.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
    }

    private void SetSummaryUnavailable()
    {
        SummaryText.Text = "CARE operations health is unavailable. No empty or healthy queue state is inferred.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
    }

    private void SetActionsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        var canProcess = enabled && current is { CanProcessDue: true };
        ProcessApprovalBox.IsEnabled = canProcess;
        ProcessButton.IsEnabled = canProcess;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value is null ? "none" : value.Value.ToUniversalTime().ToString("O");
}
