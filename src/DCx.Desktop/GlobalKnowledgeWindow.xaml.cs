using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DCx.Core.GlobalKnowledge;

namespace DCx.Desktop;

internal enum GlobalKnowledgeSourceKind
{
    Unavailable,
    LocalValidationFixture,
    ConfiguredReplica,
    EnrollmentInvalid,
}

public partial class GlobalKnowledgeWindow : Window
{
    private readonly IGlobalKnowledgeViewSource source;
    private readonly GlobalKnowledgeSourceKind sourceKind;
    private readonly IGlobalRegistryEnrollmentStore? enrollmentStore;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private bool configured;
    private bool canSynchronize;
    private bool enrollmentEditing;
    private bool enrollmentChanged;
    private bool closed;
    private GlobalRegistryEnrollmentWindow? enrollmentWindow;

    public GlobalKnowledgeWindow()
        : this(new UnavailableGlobalKnowledgeViewSource(), GlobalKnowledgeSourceKind.Unavailable)
    {
    }

    internal GlobalKnowledgeWindow(
        IGlobalKnowledgeViewSource source,
        GlobalKnowledgeSourceKind sourceKind,
        IGlobalRegistryEnrollmentStore? enrollmentStore = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.sourceKind = sourceKind;
        this.enrollmentStore = enrollmentStore;
        InitializeComponent();
        BoundaryText.Text = sourceKind switch
        {
            GlobalKnowledgeSourceKind.LocalValidationFixture =>
                "Deterministic local validation fixture. The catalog is signed and stored in a separate global replica. The resources are synthetic. Network requests, private-knowledge access, contributions and publication are unavailable.",
            GlobalKnowledgeSourceKind.ConfiguredReplica =>
                "Configured signature-verified global replica. Search is local and offline. Synchronization occurs only after you invoke it; private knowledge, contribution and publication are outside this window.",
            GlobalKnowledgeSourceKind.EnrollmentInvalid =>
                "Protected global registry enrollment could not be opened safely. No trust root, endpoint, remote state or empty catalog is inferred. Review or clear enrollment before retrying.",
            _ =>
                "No trusted global registry is configured. No remote content or empty catalog is inferred. Synchronization, contribution and publication are unavailable.",
        };
        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        EnrollmentButton.Visibility = enrollmentStore is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Heading.Focus();
        await RefreshAsync(moveFocus: false);
        if (!closed) Heading.Focus();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync(moveFocus: true);

    private async void OnSynchronizeClick(object sender, RoutedEventArgs e)
    {
        if (!configured || !canSynchronize) return;
        var token = BeginOperation("Synchronizing and verifying signed global updates.");
        if (token is null) return;
        try
        {
            var result = await source.SynchronizeAsync(token.Value);
            await LoadStatusAsync(token.Value);
            SetStatus(result.WasAlreadyCurrent
                ? $"The verified replica is already current at release {result.Status.Sequence}. Downloaded {result.BytesDownloaded} bytes."
                : $"Applied {result.ReleasesApplied} signed releases. The active release is {result.Status.Sequence}. Downloaded {result.BytesDownloaded} bytes.");
            if (!closed) SummaryText.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Global synchronization was canceled. The last atomically activated replica remains available.");
        }
        catch
        {
            if (!closed) SetStatus("Global synchronization failed closed. No unverified release was activated.");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (!configured) return;
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            SetStatus("Enter one or more search words. No search was run.");
            QueryBox.Focus();
            return;
        }

        var token = BeginOperation("Searching the verified local global catalog. No network request is occurring.");
        if (token is null) return;
        try
        {
            var results = await source.SearchAsync(query, 20, token.Value);
            ResultsList.ItemsSource = results.Select(item => new GlobalResourceListItem(item)).ToArray();
            ResultDetails.Text = results.Count == 0
                ? "No matching resource was found in the active verified local catalog."
                : "Select a result to review its signed catalog metadata.";
            AutomationProperties.SetName(ResultDetails, ResultDetails.Text);
            SetStatus(string.Create(
                CultureInfo.InvariantCulture,
                $"Offline search complete. Results: {results.Count}. Network requests: 0. Private collections searched: 0."));
            if (!closed) ResultsList.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Global knowledge search was canceled.");
        }
        catch
        {
            if (!closed)
            {
                ResultsList.ItemsSource = null;
                SetStatus("Global knowledge search failed closed. No result was inferred.");
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not GlobalResourceListItem item) return;
        ResultDetails.Text = item.Details;
        AutomationProperties.SetName(ResultDetails, item.Details);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();

    private void OnEnrollmentClick(object sender, RoutedEventArgs e)
    {
        if (enrollmentStore is null || operation is not null || enrollmentChanged) return;
        if (enrollmentWindow is { IsVisible: true })
        {
            enrollmentWindow.Activate();
            return;
        }

        enrollmentEditing = true;
        RefreshControls();
        enrollmentWindow = new GlobalRegistryEnrollmentWindow(enrollmentStore)
        {
            Owner = this,
        };
        enrollmentWindow.EnrollmentChanged += OnEnrollmentChanged;
        enrollmentWindow.Closed += (_, _) =>
        {
            if (enrollmentWindow is not null)
            {
                enrollmentWindow.EnrollmentChanged -= OnEnrollmentChanged;
            }
            enrollmentWindow = null;
            enrollmentEditing = false;
            if (!closed)
            {
                RefreshControls();
                if (enrollmentChanged)
                {
                    Heading.Focus();
                }
                else
                {
                    EnrollmentButton.Focus();
                }
            }
        };
        enrollmentWindow.Show();
    }

    private void OnEnrollmentChanged(object? sender, EventArgs e)
    {
        enrollmentChanged = true;
        configured = false;
        canSynchronize = false;
        ResultsList.ItemsSource = null;
        SetStatus("Registry enrollment changed. Search and synchronization are disabled in this window. Close and reopen Global knowledge to load the exact protected enrollment.");
        RefreshControls();
    }

    private async Task RefreshAsync(bool moveFocus)
    {
        var token = BeginOperation("Reading the verified local global replica status.");
        if (token is null) return;
        try
        {
            await LoadStatusAsync(token.Value);
            if (moveFocus && !closed) SummaryText.Focus();
        }
        catch (OperationCanceledException)
        {
            if (!closed) SetStatus("Global replica status refresh was canceled.");
        }
        catch
        {
            if (!closed)
            {
                SetUnavailableSummary();
                SetStatus("Global replica status failed closed. No catalog state was inferred.");
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task LoadStatusAsync(CancellationToken cancellationToken)
    {
        var status = await source.GetStatusAsync(cancellationToken);
        if (closed) return;
        configured = status.IsConfigured;
        canSynchronize = status.CanSynchronize;
        if (!configured || status.Replica is null)
        {
            SetUnavailableSummary();
            SetStatus("Global knowledge is not configured. No remote state, empty catalog or synchronization success was inferred.");
            return;
        }

        var policy = status.TrustPolicy is null
            ? "No rotated trust policy; releases verify directly under the configured root."
            : $"Trust policy revision: {status.TrustPolicy.Revision}. Authorized release keys: {status.TrustPolicy.ReleaseKeyCount}. Policy SHA-256: {status.TrustPolicy.Sha256}.";
        SummaryText.Text =
            $"Active signed release: {status.Replica.Sequence}.{Environment.NewLine}" +
            $"Resources: {status.Replica.ResourceCount}.{Environment.NewLine}" +
            $"Release SHA-256: {status.Replica.ReleaseSha256 ?? "none"}.{Environment.NewLine}" +
            $"{policy}{Environment.NewLine}" +
            $"Synchronization available: {(canSynchronize ? "yes, by explicit action" : "no; offline search only")}.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        SetStatus(status.Replica.Sequence == 0
            ? "The verified local global replica contains no active release. This does not establish that the authoritative catalog is empty."
            : $"Verified local global replica ready. Active release {status.Replica.Sequence}. Resources {status.Replica.ResourceCount}.");
    }

    private void SetUnavailableSummary()
    {
        configured = false;
        canSynchronize = false;
        ResultsList.ItemsSource = null;
        SummaryText.Text =
            "Global knowledge is unavailable. No authoritative catalog state, release count or resource count is inferred.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
    }

    private CancellationToken? BeginOperation(string status)
    {
        if (operation is not null || closed) return null;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        RefreshControls();
        SetStatus(status);
        return operation.Token;
    }

    private void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        if (!closed) RefreshControls();
    }

    private void RefreshControls()
    {
        var idle = operation is null && !enrollmentEditing && !enrollmentChanged;
        RefreshButton.IsEnabled = idle;
        SearchButton.IsEnabled = idle && configured;
        SynchronizeButton.IsEnabled = idle && configured && canSynchronize;
        CancelButton.IsEnabled = !idle;
        QueryBox.IsEnabled = idle && configured;
        EnrollmentButton.IsEnabled = operation is null && !enrollmentChanged;
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
        enrollmentWindow?.Close();
        operation?.Cancel();
        operation?.Dispose();
        if (source is IDisposable disposable) disposable.Dispose();
        lifetime.Dispose();
    }

    private sealed class GlobalResourceListItem
    {
        public GlobalResourceListItem(GlobalResourceRecord resource)
        {
            Resource = resource;
            var state = resource.Deprecated ? "Deprecated" : "Available";
            Display = $"{resource.Title}. {resource.Kind}. Version {resource.Version}. {state}.";
            Details =
                $"Title: {resource.Title}.{Environment.NewLine}" +
                $"Kind: {resource.Kind}.{Environment.NewLine}" +
                $"Version: {resource.Version}.{Environment.NewLine}" +
                $"Resource ID: {resource.ResourceId}.{Environment.NewLine}" +
                $"Status: {state}.{Environment.NewLine}" +
                $"Summary: {resource.Summary}{Environment.NewLine}" +
                $"License: {resource.License}.{Environment.NewLine}" +
                $"Source: {resource.SourceUri}.{Environment.NewLine}" +
                $"Capabilities: {(resource.Capabilities.Count == 0 ? "none" : string.Join(", ", resource.Capabilities))}.{Environment.NewLine}" +
                $"Artifact: {(resource.Artifact is null ? "none" : $"SHA-256 {resource.Artifact.Sha256}; {resource.Artifact.SizeBytes} bytes; {resource.Artifact.MediaType}")}.";
        }

        public GlobalResourceRecord Resource { get; }

        public string Display { get; }

        public string Details { get; }

        public override string ToString() => Display;
    }
}
