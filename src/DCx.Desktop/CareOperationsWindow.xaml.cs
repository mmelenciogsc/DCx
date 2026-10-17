using System.Windows;
using System.Windows.Automation;
using DCx.Core.Finance;

namespace DCx.Desktop;

public partial class CareOperationsWindow : Window
{
    private readonly ICareOperationsSource source;
    private readonly ICareBlockedCaseReviewSource blockedCaseSource;
    private readonly bool isFixture;
    private readonly CancellationTokenSource lifetime = new();
    private CareOperationsHealth? current;
    private bool actionsEnabled;
    private bool closed;

    public CareOperationsWindow(ICareOperationsSource source, bool isFixture)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        blockedCaseSource = source as ICareBlockedCaseReviewSource ??
            UnavailableCareBlockedCaseReviewSource.Instance;
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

    private async void OnLoadBlockedCasesClick(object sender, RoutedEventArgs e) =>
        await LoadBlockedCasesAsync();

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

    private async Task LoadBlockedCasesAsync()
    {
        SetActionsEnabled(false);
        ResetDispositionApproval();
        BlockedCasesList.ItemsSource = null;
        SetBlockedCaseDetails("Loading privacy-minimized blocked cases.");
        try
        {
            var cases = await blockedCaseSource.ListBlockedCasesAsync(lifetime.Token);
            CareBlockedCaseReviewPolicy.RequireList(cases);
            if (closed) return;
            var items = cases.Select(item => new BlockedCaseListItem(item)).ToArray();
            BlockedCasesList.ItemsSource = items;
            SetBlockedCaseDetails(items.Length == 0
                ? "No blocked CARE cases are available for review. No provider request occurred."
                : $"Loaded {items.Length} privacy-minimized blocked CARE case records. Select one to review it. No provider request occurred.");
            SetStatus(items.Length == 0
                ? "No blocked cases require disposition. No provider request occurred."
                : $"Loaded {items.Length} blocked cases. Select one, choose a non-financial disposition, and approve the exact case before recording.");
            BlockedCasesList.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                SetBlockedCaseDetails(
                    "Blocked-case review is unavailable. No empty case state was inferred.");
                SetStatus(
                    "Blocked-case review failed closed. No financial mutation or provider request occurred.");
            }
        }
        finally
        {
            if (!closed) SetActionsEnabled(true);
        }
    }

    private void OnBlockedCaseSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ResetDispositionApproval();
        if (BlockedCasesList.SelectedItem is not BlockedCaseListItem selected)
        {
            SetBlockedCaseDetails("Load and select a blocked case to review it.");
            SetBlockedActionsEnabled();
            return;
        }

        var item = selected.Review;
        SetBlockedCaseDetails(
            $"Opaque case reference: {item.CaseId}.{Environment.NewLine}" +
            $"Event kind: {item.EventKind}.{Environment.NewLine}" +
            $"Reason: {item.ReasonCode}.{Environment.NewLine}" +
            $"Attempt count: {item.AttemptCount}.{Environment.NewLine}" +
            $"First received UTC: {item.FirstReceivedUtc:O}.{Environment.NewLine}" +
            $"Last attempted UTC: {item.LastAttemptUtc:O}.{Environment.NewLine}" +
            $"Review digest: {item.ReviewSha256}.{Environment.NewLine}" +
            $"Current disposition: {item.DispositionCode}.{Environment.NewLine}" +
            "This review has no authority to change the blocked event or any financial state.");
        SetBlockedActionsEnabled();
    }

    private async void OnRecordDispositionClick(object sender, RoutedEventArgs e)
    {
        if (BlockedCasesList.SelectedItem is not BlockedCaseListItem selected ||
            DispositionApprovalBox.IsChecked != true ||
            InvestigationDisposition.IsChecked != true && RetainDisposition.IsChecked != true)
        {
            ResetDispositionApproval();
            SetStatus(
                "No disposition was recorded. Select and review one exact case, choose a disposition, and explicitly approve it first.");
            DispositionApprovalBox.Focus();
            return;
        }

        var disposition = InvestigationDisposition.IsChecked == true
            ? CareBlockedCaseReviewPolicy.InvestigationRequested
            : CareBlockedCaseReviewPolicy.RetainedBlocked;
        SetActionsEnabled(false);
        try
        {
            var result = await blockedCaseSource.RecordDispositionAsync(
                new CareBlockedCaseDispositionRequest(
                    selected.Review.CaseId,
                    selected.Review.ReviewSha256,
                    disposition,
                    ExplicitlyApproved: true),
                lifetime.Token);
            CareBlockedCaseReviewPolicy.RequireResult(result);
            if (closed) return;
            ResetDispositionApproval();
            selected.Review = selected.Review with
            {
                DispositionCode = result.DispositionCode,
            };
            BlockedCasesList.Items.Refresh();
            OnBlockedCaseSelectionChanged(
                BlockedCasesList,
                new System.Windows.Controls.SelectionChangedEventArgs(
                    System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                    Array.Empty<object>(),
                    Array.Empty<object>()));
            SetStatus(
                $"Non-financial disposition recorded as {result.DispositionCode}. Provider calls {result.ProviderCalls}. Financial mutations {result.FinancialMutations}. The event remains blocked.");
            BlockedCaseDetails.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                ResetDispositionApproval();
                SetStatus(
                    "No disposition was recorded. The request failed closed with no financial mutation or provider request.");
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
        actionsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        var canProcess = enabled && current is { CanProcessDue: true };
        ProcessApprovalBox.IsEnabled = canProcess;
        ProcessButton.IsEnabled = canProcess;
        LoadBlockedCasesButton.IsEnabled = enabled;
        SetBlockedActionsEnabled();
    }

    private void SetBlockedActionsEnabled()
    {
        var hasSelection = actionsEnabled && BlockedCasesList.SelectedItem is BlockedCaseListItem;
        InvestigationDisposition.IsEnabled = hasSelection;
        RetainDisposition.IsEnabled = hasSelection;
        DispositionApprovalBox.IsEnabled = hasSelection;
        RecordDispositionButton.IsEnabled = hasSelection;
    }

    private void ResetDispositionApproval()
    {
        DispositionApprovalBox.IsChecked = false;
        InvestigationDisposition.IsChecked = false;
        RetainDisposition.IsChecked = false;
    }

    private void SetBlockedCaseDetails(string value)
    {
        BlockedCaseDetails.Text = value;
        AutomationProperties.SetName(BlockedCaseDetails, value);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value is null ? "none" : value.Value.ToUniversalTime().ToString("O");

    private sealed class BlockedCaseListItem(CareBlockedCaseReview review)
    {
        public CareBlockedCaseReview Review { get; set; } = review;

        public string Display =>
            $"{Review.EventKind}. {Review.ReasonCode}. {Review.DispositionCode}. Case {Review.CaseId}.";
    }
}
