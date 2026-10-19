using System.Windows;
using System.Windows.Automation;
using DCx.Core.GlobalKnowledge;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class GlobalRegistryEnrollmentWindow : Window
{
    private readonly IGlobalRegistryEnrollmentStore store;
    private readonly GlobalRegistryEnrollmentReviewer reviewer;
    private readonly bool validationFixture;
    private readonly CancellationTokenSource lifetime = new();
    private GlobalRegistryEnrollmentReview? review;
    private bool busy;
    private bool closed;

    internal GlobalRegistryEnrollmentWindow(
        IGlobalRegistryEnrollmentStore store,
        GlobalRegistryEnrollmentReviewer? reviewer = null,
        bool validationFixture = false)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.reviewer = reviewer ?? new GlobalRegistryEnrollmentReviewer();
        this.validationFixture = validationFixture;
        InitializeComponent();
        if (validationFixture)
        {
            BoundaryText.Text =
                "Deterministic enrollment UI validation fixture. The candidate is stored only in this process. No Windows credential, registry request, server state, private knowledge, contribution or publication is used.";
            AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        }
        Loaded += (_, _) => Heading.Focus();
        Closed += OnClosed;
    }

    public event EventHandler? EnrollmentChanged;

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the global registry P-256 trust root",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "Public key files (*.spki;*.der;*.pub)|*.spki;*.der;*.pub|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            TrustRootPathBox.Text = dialog.FileName;
            RegistryBaseUriBox.Focus();
        }
    }

    private async void OnReviewClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        BeginOperation("Reviewing the exact trust root and registry address. No network request is occurring.");
        try
        {
            review?.Dispose();
            review = await reviewer.ReviewAsync(
                TrustRootPathBox.Text,
                RegistryBaseUriBox.Text,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            ReviewText.Text =
                $"Trust-root file: {review.FileName}.{Environment.NewLine}" +
                $"Trust-root bytes: {review.TrustRootBytes}.{Environment.NewLine}" +
                $"Trust-root SHA-256: {review.TrustRootSha256}.{Environment.NewLine}" +
                $"Registry base address: {review.RegistryBaseUri.AbsoluteUri}.{Environment.NewLine}" +
                $"Reviewed UTC: {review.ReviewedUtc:O}.{Environment.NewLine}" +
                "Network requests during review: 0.";
            AutomationProperties.SetName(ReviewText, ReviewText.Text);
            TrustRootPathBox.Text = review.FileName;
            TrustRootPathBox.IsEnabled = false;
            RegistryBaseUriBox.Text = review.RegistryBaseUri.AbsoluteUri;
            RegistryBaseUriBox.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            ReviewButton.IsEnabled = false;
            ChangeCandidateButton.IsEnabled = true;
            ApproveBox.IsEnabled = true;
            SetStatus("Review complete. Approval remains unchecked. Verify the SHA-256 fingerprint before approving.");
            ReviewText.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Global registry enrollment review was canceled.");
        }
        catch
        {
            InvalidateReview("Review failed closed. No enrollment authority changed.");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (busy || review is null || ApproveBox.IsChecked != true) return;
        BeginOperation("Saving the exact approved enrollment in protected per-user storage.");
        try
        {
            await store.SaveAsync(review, true, DateTimeOffset.UtcNow, lifetime.Token);
            EnrollmentChanged?.Invoke(this, EventArgs.Empty);
            InvalidateReview(validationFixture
                ? "Enrollment saved in the isolated validation store. Network requests: 0."
                : "Enrollment saved in Windows Credential Manager. Network requests: 0. Close and reopen Global knowledge to use it.");
            ConfirmClearBox.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Saving global registry enrollment was canceled. Existing authority is unchanged.");
        }
        catch
        {
            if (!closed) SetStatus("Global registry enrollment was not saved. Existing authority is unchanged.");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (busy || ConfirmClearBox.IsChecked != true) return;
        BeginOperation("Removing protected global registry enrollment authority.");
        try
        {
            await store.ClearAsync(lifetime.Token);
            ConfirmClearBox.IsChecked = false;
            EnrollmentChanged?.Invoke(this, EventArgs.Empty);
            SetStatus("Global registry enrollment was cleared. Network requests: 0. No cached catalog or private knowledge was deleted.");
            ConfirmClearBox.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Clearing global registry enrollment was canceled. Existing authority is unchanged.");
        }
        catch
        {
            if (!closed) SetStatus("Global registry enrollment was not cleared. Existing authority is unchanged.");
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnCandidateChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (review is not null && !busy)
        {
            InvalidateReview("Candidate input changed. Review and approval were cleared.");
        }
    }

    private void OnChangeCandidateClick(object sender, RoutedEventArgs e)
    {
        if (busy || review is null) return;
        InvalidateReview("Candidate review and approval were cleared. Enter or choose the replacement candidate.");
        TrustRootPathBox.Focus();
    }

    private void OnApprovalChanged(object sender, RoutedEventArgs e) =>
        SaveButton.IsEnabled = !busy && review is not null && ApproveBox.IsChecked == true;

    private void OnClearApprovalChanged(object sender, RoutedEventArgs e) =>
        ClearButton.IsEnabled = !busy && ConfirmClearBox.IsChecked == true;

    private void InvalidateReview(string status)
    {
        review?.Dispose();
        review = null;
        ApproveBox.IsChecked = false;
        ApproveBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ChangeCandidateButton.IsEnabled = false;
        TrustRootPathBox.IsEnabled = true;
        RegistryBaseUriBox.IsEnabled = true;
        BrowseButton.IsEnabled = !busy;
        ReviewButton.IsEnabled = !busy;
        ReviewText.Text = "No approved candidate is active.";
        AutomationProperties.SetName(ReviewText, ReviewText.Text);
        SetStatus(status);
    }

    private void BeginOperation(string status)
    {
        busy = true;
        RefreshControls();
        SetStatus(status);
    }

    private void EndOperation()
    {
        busy = false;
        if (!closed) RefreshControls();
    }

    private void RefreshControls()
    {
        var editable = review is null;
        BrowseButton.IsEnabled = !busy && editable;
        ReviewButton.IsEnabled = !busy && editable;
        TrustRootPathBox.IsEnabled = !busy && editable;
        RegistryBaseUriBox.IsEnabled = !busy && editable;
        ApproveBox.IsEnabled = !busy && review is not null;
        ChangeCandidateButton.IsEnabled = !busy && review is not null;
        SaveButton.IsEnabled = !busy && review is not null && ApproveBox.IsChecked == true;
        ConfirmClearBox.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy && ConfirmClearBox.IsChecked == true;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        review?.Dispose();
        lifetime.Dispose();
    }
}
