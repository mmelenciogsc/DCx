using System.Windows;
using System.Windows.Controls;
using DCx.Core.Finance;

namespace DCx.Desktop;

public partial class CareRecipientsWindow : Window
{
    private readonly PayoutRecipientSnapshotStore store;
    private readonly CancellationTokenSource lifetime = new();
    private PayoutRecipientCandidateReview? reviewedCandidate;
    private bool closed;

    public CareRecipientsWindow(PayoutRecipientSnapshotStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
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
        await RefreshAsync("Recipient snapshots loaded. No PayPal request occurred.");
    }

    private void OnCandidateChanged(object sender, TextChangedEventArgs e)
    {
        if (ApprovalBox is null || ReviewText is null) return;
        reviewedCandidate = null;
        ApprovalBox.IsChecked = false;
        ReviewText.Text = "The recipient input changed. Review the exact candidate again before approval.";
        ReviewText.SetCurrentValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            ReviewText.Text);
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            reviewedCandidate = PayoutRecipientSnapshotStore.ReviewCandidate(
                ContributorBox.Text,
                DestinationBox.Text);
            ApprovalBox.IsChecked = false;
            ReviewText.Text =
                $"Contributor pseudonym: {reviewedCandidate.ContributorPseudonym}{Environment.NewLine}" +
                $"Exact recipient email: {reviewedCandidate.ExactDestination}{Environment.NewLine}" +
                $"Destination SHA-256: {reviewedCandidate.DestinationSha256}{Environment.NewLine}" +
                "Owner approval status: not yet approved. PayPal verification and eligibility: not established.";
            ReviewText.SetCurrentValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                ReviewText.Text);
            SetStatus("Exact recipient candidate reviewed locally. Check the approval box only after confirming every value. No network request occurred.");
            ApprovalBox.Focus();
        }
        catch
        {
            reviewedCandidate = null;
            ApprovalBox.IsChecked = false;
            ReviewText.Text = "Recipient review blocked. Enter one valid contributor pseudonym and exact email address.";
            ReviewText.SetCurrentValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                ReviewText.Text);
            SetStatus("Recipient review blocked safely. Nothing was saved or sent.");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var current = TryReviewCurrent();
        if (reviewedCandidate is null || current is null || current != reviewedCandidate ||
            ApprovalBox.IsChecked != true)
        {
            ApprovalBox.IsChecked = false;
            SetStatus("Recipient snapshot was not saved. Review the unchanged exact values and explicitly approve them first.");
            ApprovalBox.Focus();
            return;
        }

        SetActionsEnabled(false);
        try
        {
            var approved = await store.ApproveAsync(
                current.ContributorPseudonym,
                current.ExactDestination,
                explicitlyApproved: true,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            if (closed) return;
            reviewedCandidate = null;
            ApprovalBox.IsChecked = false;
            ContributorBox.Clear();
            DestinationBox.Clear();
            ReviewText.Text = "The approved exact email was cleared from this window.";
            ReviewText.SetCurrentValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                ReviewText.Text);
            await RefreshAsync(
                $"Snapshot {approved.SnapshotId} saved in protected local storage. It is not PayPal-verified or payout-eligible. No network request occurred.");
            SnapshotsList.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                ApprovalBox.IsChecked = false;
                SetStatus("Recipient snapshot was not saved. Protected storage or validation failed safely; no network request occurred.");
            }
        }
        finally
        {
            if (!closed) SetActionsEnabled(true);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync("Recipient snapshot metadata refreshed locally. No PayPal request occurred.");

    private void OnSnapshotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RevokeApprovalBox is not null) RevokeApprovalBox.IsChecked = false;
    }

    private async void OnRevokeClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotListItem selected ||
            !selected.Summary.IsActive || RevokeApprovalBox.IsChecked != true)
        {
            RevokeApprovalBox.IsChecked = false;
            SetStatus("No snapshot was revoked. Select one active snapshot and explicitly approve revocation first.");
            RevokeApprovalBox.Focus();
            return;
        }

        SetActionsEnabled(false);
        try
        {
            await store.RevokeAsync(
                selected.Summary.SnapshotId,
                explicitlyApproved: true,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            if (closed) return;
            RevokeApprovalBox.IsChecked = false;
            await RefreshAsync(
                $"Snapshot {selected.Summary.SnapshotId} revoked. Its protected email was deleted. No PayPal request occurred.");
            SnapshotsList.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                RevokeApprovalBox.IsChecked = false;
                SetStatus("Snapshot revocation failed safely. It cannot be used while protected state is inconsistent.");
            }
        }
        finally
        {
            if (!closed) SetActionsEnabled(true);
        }
    }

    private async Task RefreshAsync(string successStatus)
    {
        try
        {
            var snapshots = await store.ListAsync(lifetime.Token);
            if (closed) return;
            SnapshotsList.ItemsSource = snapshots.Select(item => new SnapshotListItem(
                item,
                item.IsActive
                    ? $"Active owner-approved snapshot. Contributor {item.ContributorPseudonym}. Snapshot {item.SnapshotId}. Destination digest {item.DestinationSha256}. Approved {item.ApprovedUtc:O}. PayPal verification and eligibility not established."
                    : $"Revoked snapshot. Contributor {item.ContributorPseudonym}. Snapshot {item.SnapshotId}. Destination digest {item.DestinationSha256}. Approved {item.ApprovedUtc:O}. Revoked {item.RevokedUtc:O}. Protected email deleted."))
                .ToArray();
            SetStatus($"{successStatus} {snapshots.Count} minimized snapshot records are listed.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                SnapshotsList.ItemsSource = null;
                SetStatus("Recipient snapshot metadata could not be read safely. No payout recipient is available.");
            }
        }
    }

    private PayoutRecipientCandidateReview? TryReviewCurrent()
    {
        try
        {
            return PayoutRecipientSnapshotStore.ReviewCandidate(
                ContributorBox.Text,
                DestinationBox.Text);
        }
        catch
        {
            return null;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        ReviewButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        RevokeButton.IsEnabled = enabled;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        StatusText.SetCurrentValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            value);
    }

    private sealed record SnapshotListItem(
        PayoutRecipientSnapshotSummary Summary,
        string Display)
    {
        public override string ToString() => Display;
    }
}
