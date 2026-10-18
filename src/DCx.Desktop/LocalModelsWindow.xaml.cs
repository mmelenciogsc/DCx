using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DCx.Core.LocalModels;
using DCx.Core.Storage;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class LocalModelsWindow : Window
{
    private static readonly Uri OllamaOrigin = new("http://localhost:11434/");
    private readonly LocalModelSelectionStore store;
    private readonly WhisperBackendSelectionStore whisperStore;
    private readonly WhisperBackendReviewer whisperReviewer = new();
    private readonly FfmpegBackendSelectionStore ffmpegStore;
    private readonly FfmpegBackendReviewer ffmpegReviewer = new();
    private readonly LocalModelDependencyCatalogLoader dependencyCatalogLoader = new();
    private readonly LocalModelDependencyArtifactReviewer dependencyArtifactReviewer = new();
    private readonly WhisperRuntimeStager whisperRuntimeStager;
    private ILocalModelDependencyTransferSetCoordinator? dependencyTransferCoordinator;
    private readonly ILocalModelDependencyRedirectReviewer? dependencyRedirectReviewer;
    private readonly string applicationDataRoot;
    private readonly LocalModelDependencyCatalogReview? dependencyCatalogFixture;
    private readonly CancellationTokenSource lifetime = new();
    private CandidateRow? reviewedCandidate;
    private string? reviewedLicenseSha256;
    private WhisperBackendReview? reviewedWhisperBackend;
    private FfmpegBackendReview? reviewedFfmpegBackend;
    private DependencyCandidateRow? selectedDependencyCandidate;
    private LocalModelDependencyArtifactReview? reviewedDependencyArtifact;
    private LocalHardwareProfile? hardwareProfile;
    private LocalModelDependencyTransferPlan? dependencyTransferPlan;
    private LocalModelDependencyRedirectPolicyReview? dependencyRedirectPolicyReview;
    private LocalModelDependencyRedirectReviewSession? dependencyRedirectReviewSession;
    private CancellationTokenSource? dependencyRouteReviewCancellation;
    private Task? dependencyRouteReviewOperation;
    private bool dependencyRouteReviewRunning;
    private CancellationTokenSource? dependencyTransferCancellation;
    private Task? dependencyTransferOperation;
    private bool dependencyTransferRunning;
    private LocalModelDependencyTransferSetStage? lastAnnouncedTransferStage;
    private int lastAnnouncedTransferItem;

    public LocalModelsWindow(
        LocalModelSelectionStore store,
        WhisperBackendSelectionStore whisperStore,
        FfmpegBackendSelectionStore ffmpegStore)
        : this(
            store,
            whisperStore,
            ffmpegStore,
            ApplicationDataRoot.Resolve(),
            dependencyCatalogFixture: null,
            timeProvider: null,
            stageAnchorStore: null,
            enableDependencyTransfer: true,
            dependencyTransferCoordinator: null)
    {
    }

    internal LocalModelsWindow(
        LocalModelSelectionStore store,
        WhisperBackendSelectionStore whisperStore,
        FfmpegBackendSelectionStore ffmpegStore,
        string applicationDataRoot,
        LocalModelDependencyCatalogReview? dependencyCatalogFixture,
        TimeProvider? timeProvider,
        IWhisperRuntimeStageAnchorStore? stageAnchorStore,
        bool enableDependencyTransfer = false,
        ILocalModelDependencyTransferSetCoordinator? dependencyTransferCoordinator = null,
        ILocalModelDependencyRedirectReviewer? dependencyRedirectReviewer = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.whisperStore = whisperStore ?? throw new ArgumentNullException(nameof(whisperStore));
        this.ffmpegStore = ffmpegStore ?? throw new ArgumentNullException(nameof(ffmpegStore));
        this.applicationDataRoot = applicationDataRoot ?? throw new ArgumentNullException(nameof(applicationDataRoot));
        this.dependencyCatalogFixture = dependencyCatalogFixture;
        this.dependencyTransferCoordinator = dependencyTransferCoordinator ??
            (enableDependencyTransfer && dependencyCatalogFixture is null
                ? new LocalModelDependencyTransferSetCoordinator(applicationDataRoot)
                : null);
        this.dependencyRedirectReviewer = dependencyRedirectReviewer ??
            (enableDependencyTransfer && dependencyCatalogFixture is null
                ? new LocalModelDependencyRedirectReviewer()
                : null);
        whisperRuntimeStager = new WhisperRuntimeStager(timeProvider, stageAnchorStore);
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadHardwareProfile();
            await LoadDependencyCatalogAsync();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            dependencyTransferCancellation?.Cancel();
            dependencyRouteReviewCancellation?.Cancel();
            var operations = new[] { dependencyTransferOperation, dependencyRouteReviewOperation }
                .Where(operation => operation is not null && !operation.IsCompleted)
                .Cast<Task>()
                .ToArray();
            if (operations.Length == 0)
            {
                this.dependencyTransferCoordinator?.Dispose();
                this.dependencyRedirectReviewer?.Dispose();
            }
            else
            {
                _ = Task.WhenAll(operations).ContinueWith(
                    _ =>
                    {
                        this.dependencyTransferCoordinator?.Dispose();
                        this.dependencyRedirectReviewer?.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        LoadHardwareProfile();
        await RefreshAsync();
    }

    private void LoadHardwareProfile()
    {
        try
        {
            hardwareProfile = new LocalHardwareProfiler().Capture(applicationDataRoot);
            SetHardwareProfile(FormatHardwareProfile(hardwareProfile));
        }
        catch
        {
            hardwareProfile = null;
            SetHardwareProfile(
                "Native hardware profile unavailable. No external process, network request, model load, download, install, or resource estimate occurred.");
        }
    }

    private void OnOpenBenchmarkReviewClick(object sender, RoutedEventArgs e)
    {
        var window = new LocalModelBenchmarkWindow(store, whisperStore, ApplicationDataRoot.Resolve())
        {
            Owner = this,
        };
        window.Show();
    }

    private async Task LoadDependencyCatalogAsync()
    {
        dependencyTransferPlan = null;
        dependencyRedirectPolicyReview = null;
        dependencyRedirectReviewSession = null;
        DependencyTransferApprovalBox.IsChecked = false;
        SetDependencyControlsEnabled(false);
        SetDependencyCatalogSummary(dependencyCatalogFixture is null
            ? "Loading and authenticating the built-in dependency catalog."
            : "Loading the gated synthetic runtime-staging catalog.");
        try
        {
            var catalog = dependencyCatalogFixture ??
                await dependencyCatalogLoader.LoadEmbeddedAsync(lifetime.Token);
            var rows = catalog.Candidates
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .Select(candidate => new DependencyCandidateRow(candidate))
                .ToArray();
            var transferPlan = new LocalModelDependencyTransferPlanner().CreateForAll(
                catalog,
                LocalModelDependencyTransferPlanner.TryGetAvailableFreeBytes(applicationDataRoot));
            var redirectPolicyReview = this.dependencyTransferCoordinator?.RedirectPolicyReview ??
                LocalModelDependencyRedirectPolicyReview.DenyAll;
            dependencyTransferPlan = transferPlan;
            dependencyRedirectPolicyReview = redirectPolicyReview;
            DependencyCandidatesList.ItemsSource = rows;
            SetDependencyCatalogSummary(dependencyCatalogFixture is null
                ? this.dependencyTransferCoordinator is null
                    ? $"Catalog {catalog.CatalogId}. SHA-256 {catalog.CatalogSha256}. " +
                      $"{rows.Length} immutable candidates. Provider transfer is unavailable; installation, pull, execution, registration, and selection actions are absent."
                    : $"Catalog {catalog.CatalogId}. SHA-256 {catalog.CatalogSha256}. " +
                      $"{rows.Length} immutable candidates. Route observation is available only through the explicit HEAD-only action. Payload contact is available only after that in-memory review and the separate default-off exact-plan transfer approval. Transferred content remains inert; installation, extraction, pull, execution, registration, and selection are not authorized."
                : $"Synthetic validation catalog {catalog.CatalogId}. SHA-256 {catalog.CatalogSha256}. " +
                  $"{rows.Length} generated candidate. Network, download, install, pull, execution, registration, and selection are unavailable.");
            SetDependencyTransferPreflight(FormatDependencyTransferPlan(
                transferPlan,
                redirectPolicyReview,
                this.dependencyTransferCoordinator is not null,
                dependencyRedirectReviewSession));
            if (this.dependencyTransferCoordinator is not null)
            {
                var removed = await this.dependencyTransferCoordinator.CleanupAbandonedAsync(lifetime.Token);
                SetDependencyTransferStatus(
                    removed == 0
                        ? "Recovery check complete under the exclusive DCx data lease. No removable abandoned partial transfer was found. Approval remains unchecked."
                        : $"Recovery check complete under the exclusive DCx data lease. Removed {removed:N0} abandoned partial transfer {(removed == 1 ? "directory" : "directories")}. Approval remains unchecked.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            DependencyCandidatesList.ItemsSource = null;
            dependencyTransferPlan = null;
            dependencyRedirectPolicyReview = null;
            DependencyTransferApprovalBox.IsChecked = false;
            SetDependencyCatalogSummary(
                "The built-in dependency catalog failed authentication and no candidate is available. No external action occurred.");
            SetDependencyTransferPreflight(
                "Transfer preflight is unavailable because the dependency catalog failed authentication. No external action occurred.");
            SetDependencyTransferStatus(
                "Dependency transfer is unavailable because exact catalog authentication or recovery failed. No provider was contacted.");
        }
        finally
        {
            SetDependencyControlsEnabled(true);
        }
    }

    private void OnDependencyTransferApprovalChanged(object sender, RoutedEventArgs e) =>
        SetDependencyTransferControlsEnabled(enabled: true);

    private async void OnReviewDependencyRoutesClick(object sender, RoutedEventArgs e)
    {
        if (dependencyRedirectReviewer is null || dependencyTransferPlan is null ||
            dependencyTransferRunning || dependencyRouteReviewRunning)
        {
            SetDependencyTransferStatus(
                "Provider-route review is unavailable until the authenticated exact plan is ready and no other dependency operation is active.");
            ReviewDependencyRoutesButton.Focus();
            return;
        }

        dependencyRouteReviewOperation = ReviewDependencyRoutesAsync(dependencyTransferPlan);
        await dependencyRouteReviewOperation;
    }

    private async Task ReviewDependencyRoutesAsync(LocalModelDependencyTransferPlan plan)
    {
        dependencyRouteReviewRunning = true;
        dependencyRouteReviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        DependencyTransferApprovalBox.IsChecked = false;
        SetDependencyTransferStatus(
            "Reviewing current provider routes with bounded HEAD requests only. No response body, payload, credential, or signed query value will be retained.");
        SetStatus("DCx is reviewing current dependency routes. No payload transfer has started.");
        SetDependencyControlsEnabled(false);
        try
        {
            var session = await dependencyRedirectReviewer!.ReviewEmbeddedAsync(
                plan,
                LocalModelDependencyRedirectReviewApproval.Create(plan, approved: true),
                dependencyRouteReviewCancellation.Token);
            if (!string.Equals(plan.PlanSha256, dependencyTransferPlan?.PlanSha256, StringComparison.Ordinal) ||
                !session.IsCurrent)
            {
                throw new InvalidOperationException("The dependency route review is no longer current.");
            }

            var nextCoordinator = new LocalModelDependencyTransferSetCoordinator(
                applicationDataRoot,
                session);
            var previousCoordinator = dependencyTransferCoordinator;
            dependencyTransferCoordinator = nextCoordinator;
            dependencyRedirectReviewSession = session;
            dependencyRedirectPolicyReview = session.PolicyReview;
            previousCoordinator?.Dispose();

            SetDependencyTransferPreflight(FormatDependencyTransferPlan(
                plan,
                session.PolicyReview,
                transferActionAvailable: true,
                session));
            SetDependencyTransferStatus(
                $"Provider-route review complete. {session.ItemCount:N0} items. {session.RequestCount:N0} HEAD requests. {session.RedirectCount:N0} exact query-free redirect transitions. {session.ExactLengthCount:N0} exact final lengths. Review expires {session.ExpiresAtUtc:u}. No response body or payload was read. Transfer approval remains unchecked.");
            SetStatus(
                "Current provider routes are ready for accessible review. A separate unchecked exact-plan transfer approval is still required.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            SetDependencyTransferStatus(
                "Provider-route review was canceled. No payload transfer started and transfer approval remains unavailable.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (LocalModelDependencyRedirectReviewException exception)
        {
            SetDependencyTransferStatus(RouteReviewFailureMessage(exception.Code));
            DependencyTransferStatus.Focus();
        }
        catch
        {
            SetDependencyTransferStatus(
                "Provider-route review failed closed. No payload transfer started and no route authority was retained.");
            DependencyTransferStatus.Focus();
        }
        finally
        {
            dependencyRouteReviewCancellation.Dispose();
            dependencyRouteReviewCancellation = null;
            dependencyRouteReviewRunning = false;
            DependencyTransferApprovalBox.IsChecked = false;
            SetDependencyControlsEnabled(true);
        }
    }

    private void OnCancelDependencyRouteReviewClick(object sender, RoutedEventArgs e)
    {
        if (!dependencyRouteReviewRunning || dependencyRouteReviewCancellation is null) return;
        CancelDependencyRouteReviewButton.IsEnabled = false;
        SetDependencyTransferStatus(
            "Canceling provider-route review. No payload transfer has started.");
        dependencyRouteReviewCancellation.Cancel();
    }

    private async void OnStartDependencyTransferClick(object sender, RoutedEventArgs e)
    {
        if (dependencyRedirectReviewer is not null &&
            (dependencyRedirectReviewSession?.IsCurrent != true ||
             dependencyTransferCoordinator is null || dependencyRedirectPolicyReview is null ||
             !string.Equals(
                 dependencyTransferCoordinator.RedirectPolicyReview.PolicySha256,
                 dependencyRedirectPolicyReview.PolicySha256,
                 StringComparison.Ordinal)))
        {
            DependencyTransferApprovalBox.IsChecked = false;
            SetDependencyTransferStatus(
                "Transfer denied because the in-memory provider-route review is missing, expired, or changed. Review current provider routes again. No payload request started.");
            ReviewDependencyRoutesButton.Focus();
            SetDependencyTransferControlsEnabled(enabled: true);
            return;
        }
        if (dependencyTransferCoordinator is null || dependencyTransferPlan is null ||
            dependencyRedirectPolicyReview is null ||
            !dependencyTransferPlan.CapacitySufficient ||
            DependencyTransferApprovalBox.IsChecked != true || dependencyTransferRunning)
        {
            SetDependencyTransferStatus(
                "Transfer denied. Authenticate the catalog. Confirm sufficient storage. Review the exact plan. Then check the one-time approval box.");
            DependencyTransferApprovalBox.Focus();
            return;
        }

        dependencyTransferOperation = RunDependencyTransferAsync(
            dependencyTransferPlan,
            dependencyRedirectPolicyReview);
        await dependencyTransferOperation;
    }

    private async Task RunDependencyTransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectPolicyReview redirectPolicyReview)
    {
        dependencyTransferRunning = true;
        lastAnnouncedTransferStage = null;
        lastAnnouncedTransferItem = 0;
        dependencyTransferCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        DependencyTransferApprovalBox.IsChecked = false;
        DependencyTransferProgress.Value = 0;
        DependencyTransferProgress.IsIndeterminate = true;
        SetDependencyTransferStatus(
            "Preparing the explicitly approved exact-plan transfer. Capacity will be checked again before private storage is created.");
        SetStatus(
            "The approved dependency transfer is starting. Content remains inert. Nothing will be installed, registered, selected, or executed.");
        SetDependencyControlsEnabled(false);
        try
        {
            var progress = new Progress<LocalModelDependencyTransferSetProgress>(SetDependencyTransferProgress);
            var receipt = await dependencyTransferCoordinator!.TransferAsync(
                plan,
                LocalModelDependencyTransferSetApproval.Create(
                    plan,
                    redirectPolicyReview,
                    approved: true),
                dependencyTransferCancellation.Token,
                progress);
            var verified = await dependencyTransferCoordinator.VerifyAsync(
                plan,
                dependencyTransferCancellation.Token,
                progress);
            if (!string.Equals(receipt.PlanSha256, verified.PlanSha256, StringComparison.Ordinal) ||
                !string.Equals(
                    receipt.RedirectPolicySha256,
                    redirectPolicyReview.PolicySha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    receipt.RedirectPolicySha256,
                    verified.RedirectPolicySha256,
                    StringComparison.Ordinal) ||
                receipt.TransferBytes != verified.TransferBytes || receipt.Files.Count != verified.Files.Count)
            {
                throw new InvalidOperationException("The completed dependency transfer receipt changed.");
            }

            DependencyTransferProgress.IsIndeterminate = false;
            DependencyTransferProgress.Value = 100;
            SetDependencyTransferStatus(
                $"Transfer complete and reverified. Plan {verified.PlanSha256}. Redirect policy {verified.RedirectPolicySha256}. {verified.Files.Count:N0} authenticated files. {verified.TransferBytes:N0} bytes. The protected set is inert. Nothing was extracted, installed, registered, selected, or executed.");
            SetStatus(
                "The approved dependency set is complete and protected. Separate exact review is still required before any staging, registration, selection, or execution.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            DependencyTransferProgress.IsIndeterminate = false;
            SetDependencyTransferStatus(
                "Transfer canceled. This operation's incomplete private state was removed. No completed set was published by the canceled operation.");
            SetStatus("The dependency transfer was canceled. Nothing was installed, registered, selected, or executed.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (LocalModelDependencyTransferException exception)
        {
            DependencyTransferProgress.IsIndeterminate = false;
            SetDependencyTransferStatus(TransferFailureMessage(exception.Code));
            SetStatus("The dependency transfer failed closed. No content was installed, registered, selected, or executed.");
            DependencyTransferStatus.Focus();
        }
        catch
        {
            DependencyTransferProgress.IsIndeterminate = false;
            SetDependencyTransferStatus(
                "Transfer or protected verification failed closed. No unverified set is usable. Review storage and the exact plan before retrying.");
            SetStatus("The dependency transfer failed closed. No content was installed, registered, selected, or executed.");
            DependencyTransferStatus.Focus();
        }
        finally
        {
            dependencyTransferCancellation.Dispose();
            dependencyTransferCancellation = null;
            dependencyTransferRunning = false;
            DependencyTransferApprovalBox.IsChecked = false;
            SetDependencyControlsEnabled(true);
        }
    }

    private void OnCancelDependencyTransferClick(object sender, RoutedEventArgs e)
    {
        if (!dependencyTransferRunning || dependencyTransferCancellation is null) return;
        CancelDependencyTransferButton.IsEnabled = false;
        SetDependencyTransferStatus(
            "Cancellation requested. DCx is stopping safely and removing only this operation's incomplete private state.");
        dependencyTransferCancellation.Cancel();
    }

    private async void OnVerifyDependencyTransferClick(object sender, RoutedEventArgs e)
    {
        dependencyTransferOperation = VerifyDependencyTransferAsync();
        await dependencyTransferOperation;
    }

    private async Task VerifyDependencyTransferAsync()
    {
        if (dependencyTransferCoordinator is null || dependencyTransferPlan is null || dependencyTransferRunning)
        {
            SetDependencyTransferStatus("Completed-set verification is unavailable until the exact plan is authenticated.");
            return;
        }

        dependencyTransferRunning = true;
        dependencyTransferCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetDependencyControlsEnabled(false);
        SetDependencyTransferStatus("Rehashing the exact protected transfer set. No content will be executed.");
        try
        {
            var progress = new Progress<LocalModelDependencyTransferSetProgress>(SetDependencyTransferProgress);
            var receipt = await dependencyTransferCoordinator.VerifyAsync(
                dependencyTransferPlan,
                dependencyTransferCancellation.Token,
                progress);
            SetDependencyTransferStatus(
                $"Completed set verified. Plan {receipt.PlanSha256}. Redirect policy {receipt.RedirectPolicySha256}. {receipt.Files.Count:N0} authenticated files. {receipt.TransferBytes:N0} bytes. Nothing was executed.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            SetDependencyTransferStatus("Completed-set verification was canceled. No content was changed or executed.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetDependencyTransferStatus(
                "Completed-set verification failed closed. The set is unavailable until its protected receipt and every payload pass again.");
            DependencyTransferStatus.Focus();
        }
        finally
        {
            dependencyTransferCancellation.Dispose();
            dependencyTransferCancellation = null;
            dependencyTransferRunning = false;
            SetDependencyControlsEnabled(true);
        }
    }

    private async void OnRecoverDependencyTransferClick(object sender, RoutedEventArgs e)
    {
        dependencyTransferOperation = RecoverDependencyTransferAsync();
        await dependencyTransferOperation;
    }

    private async Task RecoverDependencyTransferAsync()
    {
        if (dependencyTransferCoordinator is null || dependencyTransferRunning) return;
        dependencyTransferRunning = true;
        dependencyTransferCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetDependencyControlsEnabled(false);
        SetDependencyTransferStatus(
            "Checking strict incoming transfer directories under the exclusive DCx data lease. Links will be refused and left inert.");
        try
        {
            var removed = await dependencyTransferCoordinator.CleanupAbandonedAsync(
                dependencyTransferCancellation.Token);
            SetDependencyTransferStatus(
                removed == 0
                    ? "Recovery complete. No removable abandoned partial transfer was found. Unsafe linked content, if present, remains inert and untouched."
                    : $"Recovery complete. Removed {removed:N0} abandoned partial transfer {(removed == 1 ? "directory" : "directories")}. Unsafe linked content, if present, remains inert and untouched.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            SetDependencyTransferStatus("Recovery was canceled. No completed set was changed.");
            DependencyTransferStatus.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetDependencyTransferStatus(
                "Recovery failed closed. No completed set was changed. Unsafe content was not followed.");
            DependencyTransferStatus.Focus();
        }
        finally
        {
            dependencyTransferCancellation.Dispose();
            dependencyTransferCancellation = null;
            dependencyTransferRunning = false;
            SetDependencyControlsEnabled(true);
        }
    }

    private void SetDependencyTransferProgress(LocalModelDependencyTransferSetProgress value)
    {
        var percentage = value.TotalExpectedBytes <= 0
            ? 0
            : Math.Clamp(value.TotalBytesTransferred * 100d / value.TotalExpectedBytes, 0, 100);
        DependencyTransferProgress.IsIndeterminate = value.Stage is
            LocalModelDependencyTransferSetStage.Preparing or
            LocalModelDependencyTransferSetStage.ProtectingReceipt;
        DependencyTransferProgress.Value = percentage;
        var item = value.ItemNumber > 0 ? $" Item {value.ItemNumber:N0} of {value.ItemCount:N0}." : string.Empty;
        var name = $"Dependency transfer {value.Stage}.{item} {value.TotalBytesTransferred:N0} of {value.TotalExpectedBytes:N0} bytes.";
        AutomationProperties.SetName(DependencyTransferProgress, name);
        if (lastAnnouncedTransferStage == value.Stage && lastAnnouncedTransferItem == value.ItemNumber) return;
        lastAnnouncedTransferStage = value.Stage;
        lastAnnouncedTransferItem = value.ItemNumber;
        SetDependencyTransferStatus(name);
    }

    private static string TransferFailureMessage(string code) => code switch
    {
        "dependency_transfer_destination_exists" =>
            "Transfer stopped because this exact completed set already exists. Use Verify completed set. Existing content was not overwritten.",
        "dependency_transfer_capacity_unapproved" =>
            "Transfer stopped because the fresh storage-capacity check did not meet the exact plan requirement. No provider transfer began.",
        "dependency_transfer_timeout" =>
            "Transfer timed out and failed closed. Partial bytes from this operation were removed.",
        "dependency_transfer_response_invalid" =>
            "Transfer stopped because a provider response, redirect, encoding, range, or length did not match the reviewed policy. Partial bytes were removed.",
        "dependency_transfer_catalog_invalid" or "dependency_transfer_approval_invalid" =>
            "Transfer stopped because the authenticated catalog, exact plan, or one-time approval no longer matched. No unapproved transfer continued.",
        _ => "Transfer failed closed. Partial bytes from this operation were removed. No completed unverified set was published.",
    };

    private static string RouteReviewFailureMessage(string code) => code switch
    {
        "dependency_redirect_review_timeout" =>
            "Provider-route review timed out. No payload transfer started and no route authority was retained.",
        "dependency_redirect_review_response_invalid" or
        "dependency_redirect_review_redirect_invalid" or
        "dependency_redirect_review_redirect_limit" =>
            "Provider-route review stopped because a response or redirect was outside the exact bounded policy. No payload transfer started.",
        "dependency_redirect_review_catalog_invalid" or
        "dependency_redirect_review_approval_invalid" =>
            "Provider-route review stopped because the authenticated exact plan or explicit review action no longer matched. No provider payload was requested.",
        _ => "Provider-route review failed closed. No payload transfer started and no route authority was retained.",
    };

    private void OnDependencyCandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedDependencyCandidate = DependencyCandidatesList.SelectedItem as DependencyCandidateRow;
        DependencyArtifactPath.Clear();
        ResetDependencyArtifactReview("No staged artifact has been verified.");
        if (selectedDependencyCandidate is null)
        {
            SetDependencyCandidateDetails("Select a candidate to review its immutable identity.");
            SetDependencyControlsEnabled(true);
            return;
        }

        SetDependencyCandidateDetails(FormatDependencyCandidate(selectedDependencyCandidate.Candidate));
        SetDependencyControlsEnabled(true);
    }

    private void OnDependencyArtifactPathChanged(object sender, TextChangedEventArgs e)
    {
        ResetDependencyArtifactReview("No staged artifact has been verified.");
        if (ReviewDependencyArtifactButton is not null)
        {
            ReviewDependencyArtifactButton.IsEnabled =
                selectedDependencyCandidate is not null &&
                !string.IsNullOrWhiteSpace(DependencyArtifactPath.Text);
        }
    }

    private void OnBrowseDependencyArtifactClick(object sender, RoutedEventArgs e)
    {
        if (selectedDependencyCandidate is null)
        {
            SetStatus("Select an audited dependency candidate before choosing an already-staged local artifact.");
            DependencyCandidatesList.Focus();
            return;
        }

        var filter = selectedDependencyCandidate.Candidate.Kind switch
        {
            LocalModelDependencyKind.WhisperRuntimeArchive => "ZIP archives (*.zip)|*.zip",
            LocalModelDependencyKind.WhisperModel => "Whisper models (*.bin)|*.bin",
            LocalModelDependencyKind.OllamaManifest => "JSON manifests (*.json)|*.json",
            _ => "All files (*.*)|*.*",
        };
        BrowseInto(
            DependencyArtifactPath,
            filter,
            $"Select an already-staged artifact for {selectedDependencyCandidate.Candidate.DisplayName}");
    }

    private async void OnReviewDependencyArtifactClick(object sender, RoutedEventArgs e)
    {
        if (selectedDependencyCandidate is null || string.IsNullOrWhiteSpace(DependencyArtifactPath.Text))
        {
            SetStatus("Select an audited candidate and one already-staged local artifact before verification.");
            return;
        }

        var candidate = selectedDependencyCandidate.Candidate;
        var path = DependencyArtifactPath.Text;
        ResetDependencyArtifactReview("Verifying the staged artifact under a read-only lease.");
        SetDependencyControlsEnabled(false);
        SetStatus("Verifying exact staged-artifact type, length, SHA-256, and managed metadata. Nothing is downloaded, extracted, installed, pulled, or executed.");
        try
        {
            var review = await dependencyArtifactReviewer.ReviewAsync(candidate, path, lifetime.Token);
            reviewedDependencyArtifact = review;
            var runtimeInventory = review.RuntimeArchive is null
                ? string.Empty
                : $" Runtime archive: {review.RuntimeArchive.FileCount:N0} files and " +
                  $"{review.RuntimeArchive.DirectoryCount:N0} directories; {review.RuntimeArchive.ExpandedBytes:N0} expanded bytes; " +
                  $"CLI {review.RuntimeArchive.WhisperCliEntry}; " +
                  (review.RuntimeArchive.LicenseNoticeEntryCount == 0
                      ? "no contained license or notice file was found, so external and redistribution terms still require review."
                      : $"{review.RuntimeArchive.LicenseNoticeEntryCount:N0} contained license or notice files" +
                        (review.RuntimeArchive.LicenseNoticeEntryCount > review.RuntimeArchive.LicenseNoticeEntries.Count
                            ? $"; first {review.RuntimeArchive.LicenseNoticeEntries.Count:N0} shown: "
                            : ": ") +
                        $"{string.Join(", ", review.RuntimeArchive.LicenseNoticeEntries)}. All contained terms require review.");
            SetDependencyArtifactResult(
                $"Verified {review.CandidateId}. {review.Bytes:N0} bytes. SHA-256 {review.Sha256}. " +
                $"Reviewed {review.ReviewedUtc:u}.{runtimeInventory} " +
                "No installation, extraction, model pull, execution, approval, or selection occurred.");
            SetStatus("The already-staged dependency artifact matches the immutable catalog identity. This integrity result is not installation, execution, legal, or production approval.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetDependencyArtifactResult(
                "Verification failed closed. The file did not match the selected immutable identity or its path could not be reviewed safely. No external action or model-state change occurred.");
            SetStatus("The staged dependency artifact was rejected. Nothing was downloaded, extracted, installed, pulled, executed, approved, or selected.");
        }
        finally
        {
            SetDependencyControlsEnabled(true);
        }
    }

    private void OnDependencyStagingApprovalChanged(object sender, RoutedEventArgs e)
    {
        if (StageDependencyRuntimeButton is null) return;
        StageDependencyRuntimeButton.IsEnabled =
            DependencyStagingApprovalBox.IsChecked == true &&
            reviewedDependencyArtifact?.RuntimeArchive is not null;
    }

    private async void OnStageDependencyRuntimeClick(object sender, RoutedEventArgs e)
    {
        if (selectedDependencyCandidate is null || reviewedDependencyArtifact?.RuntimeArchive is null ||
            DependencyStagingApprovalBox.IsChecked != true ||
            !string.Equals(
                selectedDependencyCandidate.Candidate.Id,
                reviewedDependencyArtifact.CandidateId,
                StringComparison.Ordinal))
        {
            SetStatus("Runtime staging was denied. Verify one exact Whisper runtime archive and check the separate staging acknowledgment first.");
            DependencyStagingApprovalBox.Focus();
            return;
        }

        var candidate = selectedDependencyCandidate.Candidate;
        var source = reviewedDependencyArtifact.ArtifactPath;
        SetDependencyControlsEnabled(false);
        SetDependencyArtifactResult("Reauthenticating and staging the verified runtime as inert per-user files.");
        SetStatus("Staging the exact reviewed runtime in private DCx storage. It will not be installed, registered, executed, or selected.");
        try
        {
            var result = await whisperRuntimeStager.StageAsync(
                candidate,
                source,
                applicationDataRoot,
                explicitlyApproved: true,
                lifetime.Token);
            reviewedDependencyArtifact = null;
            DependencyStagingApprovalBox.IsChecked = false;
            SetDependencyArtifactResult(
                $"Staged {result.CandidateId} inertly. {result.FileCount:N0} files and " +
                $"{result.DirectoryCount:N0} archive directories; {result.ExpandedBytes:N0} bytes. " +
                $"Archive SHA-256 {result.ArchiveSha256}. CLI {result.WhisperCliPath}. " +
                $"Receipt {result.ReceiptPath}. Its exact receipt identity is separately protected. " +
                "No installation, registration, execution, or selection occurred.");
            SetStatus("The exact runtime and protected receipt anchor were staged inertly. Registering or executing it requires separate review and approval.");
            DependencyArtifactResult.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ResetDependencyArtifactReview(
                "Runtime staging failed closed. No runtime was activated. An inert untrusted stage may remain for later review; review the source again before retrying.");
            SetStatus("Runtime staging was rejected. Nothing was installed, registered, executed, or selected.");
        }
        finally
        {
            SetDependencyControlsEnabled(true);
        }
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApprovalBox.IsChecked = false;
        reviewedCandidate = null;
        reviewedLicenseSha256 = null;
        UseVisionButton.IsEnabled = false;
        UseEmbeddingButton.IsEnabled = false;
        SetLicense("Select an installed model to inspect its complete license text.");
        if (ModelsList.SelectedItem is not CandidateRow selected)
        {
            SetDetails("Select an installed model to review it.");
            return;
        }

        SetDetails($"Reviewing {selected.Descriptor.Name}.");
        try
        {
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var inspection = await client.InspectModelAsync(selected.Descriptor.Name, lifetime.Token);
            if (ModelsList.SelectedItem is not CandidateRow current || !ReferenceEquals(current, selected)) return;
            if (string.IsNullOrWhiteSpace(inspection.License))
            {
                SetDetails($"{selected.Display} License metadata is missing, so this model cannot be selected.");
                SetLicense("No license text was returned for this installed model.");
                return;
            }

            reviewedCandidate = selected;
            reviewedLicenseSha256 = Sha256(inspection.License);
            SetLicense(inspection.License);
            var licenseSummary = inspection.License
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "License text present";
            SetDetails(
                $"{selected.Display} License: {licenseSummary}. License SHA-256: {reviewedLicenseSha256}. " +
                (selected.EligibleVision || selected.EligibleEmbedding
                    ? "This exact installed identity is eligible for the listed capability. Check approval before selecting it."
                    : "This identity is not eligible because its name is mutable, duplicated, or does not advertise vision or embedding capability."));
            UseVisionButton.IsEnabled = selected.EligibleVision;
            UseEmbeddingButton.IsEnabled = selected.EligibleEmbedding;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetDetails("The selected model metadata could not be inspected safely. No selection changed.");
            SetLicense("The selected model license text could not be inspected safely.");
        }
    }

    private async void OnUseVisionClick(object sender, RoutedEventArgs e) =>
        await ApproveAsync(isVision: true);

    private async void OnUseEmbeddingClick(object sender, RoutedEventArgs e) =>
        await ApproveAsync(isVision: false);

    private async void OnClearVisionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await store.ClearVisionAsync(lifetime.Token);
            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus("The local vision-model selection was cleared. No model was downloaded, deleted, or contacted remotely.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The local vision-model selection could not be cleared. No model or user content was changed.");
        }
    }

    private async void OnClearEmbeddingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await store.ClearEmbeddingAsync(lifetime.Token);
            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus("The local embedding-model selection was cleared. No model was downloaded, deleted, or contacted remotely.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The local embedding-model selection could not be cleared. No model or user content was changed.");
        }
    }

    private void OnWhisperInputChanged(object sender, TextChangedEventArgs e) =>
        ResetWhisperReview();

    private void OnBrowseWhisperExecutableClick(object sender, RoutedEventArgs e) =>
        BrowseInto(WhisperExecutablePath, "Executable files (*.exe)|*.exe", "Select an already-installed Whisper executable");

    private void OnBrowseWhisperModelClick(object sender, RoutedEventArgs e) =>
        BrowseInto(WhisperModelPath, "Whisper models (*.bin;*.gguf)|*.bin;*.gguf", "Select an already-installed Whisper model");

    private void OnBrowseWhisperRuntimeLicenseClick(object sender, RoutedEventArgs e) =>
        BrowseInto(WhisperRuntimeLicensePath, "License text (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*", "Select the Whisper runtime license");

    private void OnBrowseWhisperModelLicenseClick(object sender, RoutedEventArgs e) =>
        BrowseInto(WhisperModelLicensePath, "License text (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*", "Select the Whisper model license");

    private async void OnReviewWhisperBackendClick(object sender, RoutedEventArgs e)
    {
        ResetWhisperReview();
        SetWhisperControlsEnabled(false);
        SetStatus("Reviewing and hashing the four local Whisper files. Nothing is downloaded, installed, or uploaded.");
        try
        {
            var review = await whisperReviewer.ReviewAsync(
                WhisperExecutablePath.Text,
                WhisperModelPath.Text,
                WhisperRuntimeLicensePath.Text,
                WhisperModelLicensePath.Text,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            reviewedWhisperBackend = review;
            SetWhisperReview(review);
            SetStatus("The exact local Whisper files and both complete licenses are ready for review. Registration remains unchanged until explicit approval is checked and saved.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetWhisperDetails("The four local Whisper files could not be reviewed safely. Registration was not changed.");
            SetWhisperLicense("No approved license review is available.");
            SetStatus("Whisper review failed closed. Confirm four distinct regular local files, supported extensions, size limits, and UTF-8 license text.");
        }
        finally
        {
            SetWhisperControlsEnabled(true);
        }
    }

    private async void OnSaveWhisperBackendClick(object sender, RoutedEventArgs e)
    {
        if (reviewedWhisperBackend is null || WhisperApprovalBox.IsChecked != true)
        {
            SetStatus("Whisper registration was not saved. Complete the file review and check the explicit approval box first.");
            WhisperApprovalBox.Focus();
            return;
        }

        SetWhisperControlsEnabled(false);
        SetStatus("Revalidating the four exact Whisper files before saving. Nothing is downloaded, installed, or uploaded.");
        try
        {
            var rechecked = await whisperReviewer.ReviewAsync(
                WhisperExecutablePath.Text,
                WhisperModelPath.Text,
                WhisperRuntimeLicensePath.Text,
                WhisperModelLicensePath.Text,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            if (!reviewedWhisperBackend.Binding.HasSameIdentity(rechecked.Binding))
            {
                throw new InvalidOperationException("The reviewed Whisper file identity changed.");
            }

            await whisperStore.SetAsync(rechecked.Binding, explicitlyApproved: true, lifetime.Token);
            reviewedWhisperBackend = rechecked;
            WhisperApprovalBox.IsChecked = false;
            SetWhisperReview(rechecked);
            await UpdateWhisperSelectionAsync();
            SetStatus("The exact hash-pinned Whisper backend registration was saved. Nothing was downloaded, installed, or uploaded.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ResetWhisperReview();
            SetStatus("A Whisper file or license could not be revalidated, so registration was not saved. Review all four files again.");
        }
        finally
        {
            SetWhisperControlsEnabled(true);
        }
    }

    private async void OnClearWhisperBackendClick(object sender, RoutedEventArgs e)
    {
        SetWhisperControlsEnabled(false);
        SetStatus("Clearing only the DCx Whisper registration. Source files will be preserved.");
        try
        {
            await whisperStore.ClearAsync(lifetime.Token);
            ResetWhisperReview();
            await UpdateWhisperSelectionAsync();
            SetStatus("The Whisper registration was cleared. No executable, model, license, or other local file was deleted or uninstalled.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The Whisper registration could not be cleared. No local file was changed.");
        }
        finally
        {
            SetWhisperControlsEnabled(true);
        }
    }

    private void OnFfmpegInputChanged(object sender, TextChangedEventArgs e) => ResetFfmpegReview();

    private void OnBrowseFfmpegExecutableClick(object sender, RoutedEventArgs e) =>
        BrowseInto(FfmpegExecutablePath, "Executable files (*.exe)|*.exe", "Select an already-installed FFmpeg executable");

    private void OnBrowseFfmpegLicenseClick(object sender, RoutedEventArgs e) =>
        BrowseInto(FfmpegLicensePath, "License text (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*", "Select the FFmpeg license");

    private async void OnReviewFfmpegBackendClick(object sender, RoutedEventArgs e)
    {
        ResetFfmpegReview();
        SetFfmpegControlsEnabled(false);
        SetStatus("Reviewing and hashing local FFmpeg files and reading real Windows signature status. Nothing is downloaded, installed, or uploaded.");
        try
        {
            var review = await ffmpegReviewer.ReviewAsync(
                FfmpegExecutablePath.Text, FfmpegLicensePath.Text, DateTimeOffset.UtcNow, lifetime.Token);
            reviewedFfmpegBackend = review;
            SetFfmpegReview(review);
            SetStatus("The exact FFmpeg files, complete license, and Windows signature status are ready for review. Registration remains unchanged until explicit approval.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            SetFfmpegDetails("The local FFmpeg files or Windows signature status could not be reviewed safely. Registration was not changed.");
            SetFfmpegLicense("No approved FFmpeg license review is available.");
            SetStatus("FFmpeg review failed closed. Confirm distinct regular local files, supported names, size limits, and UTF-8 license text.");
        }
        finally { SetFfmpegControlsEnabled(true); }
    }

    private async void OnSaveFfmpegBackendClick(object sender, RoutedEventArgs e)
    {
        if (reviewedFfmpegBackend is null || FfmpegApprovalBox.IsChecked != true)
        {
            SetStatus("FFmpeg registration was not saved. Complete review and check the explicit approval box first.");
            FfmpegApprovalBox.Focus();
            return;
        }
        SetFfmpegControlsEnabled(false);
        SetStatus("Revalidating exact FFmpeg files and Windows signature status before saving.");
        try
        {
            var rechecked = await ffmpegReviewer.ReviewAsync(
                FfmpegExecutablePath.Text, FfmpegLicensePath.Text, DateTimeOffset.UtcNow, lifetime.Token);
            if (!reviewedFfmpegBackend.Binding.HasSameIdentity(rechecked.Binding))
                throw new InvalidOperationException("The reviewed FFmpeg identity changed.");
            await ffmpegStore.SetAsync(rechecked.Binding, true, lifetime.Token);
            reviewedFfmpegBackend = rechecked;
            FfmpegApprovalBox.IsChecked = false;
            SetFfmpegReview(rechecked);
            await UpdateFfmpegSelectionAsync();
            SetStatus("The exact hash-pinned FFmpeg registration was saved. Nothing was downloaded, installed, bundled, or uploaded.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            ResetFfmpegReview();
            SetStatus("An FFmpeg file or signature result could not be revalidated, so registration was not saved. Review both files again.");
        }
        finally { SetFfmpegControlsEnabled(true); }
    }

    private async void OnClearFfmpegBackendClick(object sender, RoutedEventArgs e)
    {
        SetFfmpegControlsEnabled(false);
        SetStatus("Clearing only the DCx FFmpeg registration. Source files will be preserved.");
        try
        {
            await ffmpegStore.ClearAsync(lifetime.Token);
            ResetFfmpegReview();
            await UpdateFfmpegSelectionAsync();
            SetStatus("The FFmpeg registration was cleared. No executable, license, or other local file was deleted or uninstalled.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { SetStatus("The FFmpeg registration could not be cleared. No local file was changed."); }
        finally { SetFfmpegControlsEnabled(true); }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        ApprovalBox.IsChecked = false;
        reviewedCandidate = null;
        reviewedLicenseSha256 = null;
        SetDetails("Select an installed model to review it.");
        SetLicense("Select an installed model to inspect its complete license text.");
        SetControlsEnabled(false);
        SetStatus("Reading installed model identities from the loopback Ollama runtime. Nothing is downloaded or uploaded.");
        try
        {
            await UpdateWhisperSelectionAsync();
            await UpdateFfmpegSelectionAsync();
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var models = await client.ListModelsAsync(lifetime.Token);
            var resourceSelection = hardwareProfile is null
                ? null
                : new LocalModelSelector().SelectForHardware(hardwareProfile, models);
            var rows = models
                .OrderBy(model => model.Name, StringComparer.Ordinal)
                .ThenBy(model => model.Digest, StringComparer.Ordinal)
                .Select(model => new CandidateRow(
                    model,
                    IsExactModel(resourceSelection?.Vision.Model, model),
                    IsExactModel(resourceSelection?.Embedding.Model, model)))
                .ToArray();
            ModelsList.ItemsSource = rows;
            await UpdateCurrentSelectionsAsync();
            var eligible = rows.Count(row => row.EligibleVision || row.EligibleEmbedding);
            if (store.RequiresReapproval || whisperStore.RequiresReapproval || ffmpegStore.RequiresReapproval)
            {
                SetStatus(
                    "One or more stored local-model approvals did not match protected per-user anchors. They are unavailable until reviewed and explicitly approved again. Nothing was downloaded or executed.");
            }
            else
            {
                SetStatus(
                    hardwareProfile is null
                        ? $"Found {rows.Length} installed model records, but none is eligible because the native hardware profile is unavailable. Nothing was downloaded or uploaded."
                        : $"Found {rows.Length} installed model records. {eligible} resource-selected digest-bound records are eligible. " +
                          $"{resourceSelection!.Vision.Reason} {resourceSelection.Embedding.Reason} Nothing was downloaded or uploaded.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ModelsList.ItemsSource = null;
            SetStatus("Installed local models could not be read safely. No selection changed and no remote provider was used.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task ApproveAsync(bool isVision)
    {
        if (ApprovalBox.IsChecked != true || reviewedCandidate is null || reviewedLicenseSha256 is null)
        {
            SetStatus("Selection was not saved. Review a model and check the explicit approval box first.");
            ApprovalBox.Focus();
            return;
        }

        if (isVision && !reviewedCandidate.EligibleVision ||
            !isVision && !reviewedCandidate.EligibleEmbedding)
        {
            SetStatus("Selection was not saved because this model identity is not eligible for that capability.");
            return;
        }

        SetControlsEnabled(false);
        try
        {
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var inventory = await client.ListModelsAsync(lifetime.Token);
            var sameName = inventory.Where(candidate =>
                    string.Equals(candidate.Name, reviewedCandidate.Descriptor.Name, StringComparison.Ordinal))
                .ToArray();
            if (sameName.Length != 1 ||
                !string.Equals(
                    sameName[0].Digest,
                    reviewedCandidate.Descriptor.Digest,
                    StringComparison.OrdinalIgnoreCase) ||
                !sameName[0].HasDigestBoundName ||
                isVision && !sameName[0].SupportsVision || !isVision && !sameName[0].SupportsEmbeddings)
            {
                throw new InvalidOperationException("The reviewed model binding changed.");
            }

            var currentHardware = new LocalHardwareProfiler().Capture(applicationDataRoot);
            hardwareProfile = currentHardware;
            SetHardwareProfile(FormatHardwareProfile(currentHardware));
            var resourceSelection = new LocalModelSelector().SelectForHardware(currentHardware, inventory);
            var currentlyEligible = isVision
                ? resourceSelection.Vision.Model
                : resourceSelection.Embedding.Model;
            if (!IsExactModel(currentlyEligible, sameName[0]))
            {
                throw new InvalidOperationException("The reviewed model no longer matches the native resource policy.");
            }

            var inspection = await client.InspectModelAsync(sameName[0].Name, lifetime.Token);
            if (string.IsNullOrWhiteSpace(inspection.License) ||
                !string.Equals(Sha256(inspection.License), reviewedLicenseSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The reviewed model license changed.");
            }

            if (isVision)
            {
                await store.SetVisionAsync(
                    sameName[0], reviewedLicenseSha256, true, DateTimeOffset.UtcNow, lifetime.Token);
            }
            else
            {
                await store.SetEmbeddingAsync(
                    sameName[0], reviewedLicenseSha256, true, DateTimeOffset.UtcNow, lifetime.Token);
            }

            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus(
                $"The exact digest-bound local {(isVision ? "vision" : "embedding")} model selection was saved. Nothing was downloaded or uploaded.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The model identity or license could not be revalidated, so no selection was saved.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task UpdateCurrentSelectionsAsync()
    {
        var state = await store.GetStateAsync(lifetime.Token);
        if (store.RequiresReapproval)
        {
            const string warning =
                "Stored local-model approvals did not match their protected per-user anchor. Vision and embedding are unavailable until reviewed and explicitly approved again.";
            CurrentVisionText.Text = warning;
            CurrentEmbeddingText.Text = warning;
            AutomationProperties.SetName(CurrentVisionText, warning);
            AutomationProperties.SetName(CurrentEmbeddingText, warning);
            return;
        }
        SetCurrent(CurrentVisionText, "Vision", state.Vision);
        SetCurrent(CurrentEmbeddingText, "Embedding", state.Embedding);
    }

    private async Task UpdateWhisperSelectionAsync()
    {
        var state = await whisperStore.GetStateAsync(lifetime.Token);
        var binding = state.Backend;
        var value = whisperStore.RequiresReapproval
            ? "Transcription: a stored approval did not match its protected per-user anchor and is unavailable until reviewed and explicitly approved again."
            : binding is null
            ? "Transcription: none selected."
            : $"Transcription: {System.IO.Path.GetFileName(binding.ExecutablePath)} with {System.IO.Path.GetFileName(binding.ModelPath)}. " +
              $"Executable digest {binding.ExecutableSha256}. Model digest {binding.ModelSha256}. " +
              $"Runtime license digest {binding.RuntimeLicenseSha256}. Model license digest {binding.ModelLicenseSha256}. Approved {binding.ApprovedUtc:u}.";
        CurrentWhisperText.Text = value;
        AutomationProperties.SetName(CurrentWhisperText, value);
    }

    private async Task UpdateFfmpegSelectionAsync()
    {
        var binding = (await ffmpegStore.GetStateAsync(lifetime.Token)).Backend;
        var value = ffmpegStore.RequiresReapproval
            ? "Media processor: a stored approval did not match its protected per-user anchor and is unavailable until reviewed and explicitly approved again."
            : binding is null
            ? "Media processor: none selected."
            : $"Media processor: {System.IO.Path.GetFileName(binding.ExecutablePath)}. Executable digest {binding.ExecutableSha256}. " +
              $"License digest {binding.LicenseSha256}. Windows signature status {binding.AuthenticodeStatus}. Approved {binding.ApprovedUtc:u}.";
        CurrentFfmpegText.Text = value;
        AutomationProperties.SetName(CurrentFfmpegText, value);
    }

    private void SetControlsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        ModelsList.IsEnabled = enabled;
        ApprovalBox.IsEnabled = enabled;
        UseVisionButton.IsEnabled = enabled && reviewedCandidate?.EligibleVision == true;
        UseEmbeddingButton.IsEnabled = enabled && reviewedCandidate?.EligibleEmbedding == true;
    }

    private void SetWhisperControlsEnabled(bool enabled)
    {
        WhisperExecutablePath.IsEnabled = enabled;
        WhisperModelPath.IsEnabled = enabled;
        WhisperRuntimeLicensePath.IsEnabled = enabled;
        WhisperModelLicensePath.IsEnabled = enabled;
        BrowseWhisperExecutableButton.IsEnabled = enabled;
        BrowseWhisperModelButton.IsEnabled = enabled;
        BrowseWhisperRuntimeLicenseButton.IsEnabled = enabled;
        BrowseWhisperModelLicenseButton.IsEnabled = enabled;
        ReviewWhisperBackendButton.IsEnabled = enabled;
        WhisperApprovalBox.IsEnabled = enabled && reviewedWhisperBackend is not null;
        SaveWhisperBackendButton.IsEnabled = enabled && reviewedWhisperBackend is not null;
        ClearWhisperBackendButton.IsEnabled = enabled;
    }

    private void SetFfmpegControlsEnabled(bool enabled)
    {
        FfmpegExecutablePath.IsEnabled = enabled;
        FfmpegLicensePath.IsEnabled = enabled;
        BrowseFfmpegExecutableButton.IsEnabled = enabled;
        BrowseFfmpegLicenseButton.IsEnabled = enabled;
        ReviewFfmpegBackendButton.IsEnabled = enabled;
        FfmpegApprovalBox.IsEnabled = enabled && reviewedFfmpegBackend is not null;
        SaveFfmpegBackendButton.IsEnabled = enabled && reviewedFfmpegBackend is not null;
        ClearFfmpegBackendButton.IsEnabled = enabled;
    }

    private void SetDependencyControlsEnabled(bool enabled)
    {
        var reviewEnabled = enabled && !dependencyTransferRunning && !dependencyRouteReviewRunning;
        DependencyCandidatesList.IsEnabled = reviewEnabled;
        DependencyArtifactPath.IsEnabled = reviewEnabled && selectedDependencyCandidate is not null;
        BrowseDependencyArtifactButton.IsEnabled = reviewEnabled && selectedDependencyCandidate is not null;
        ReviewDependencyArtifactButton.IsEnabled =
            reviewEnabled && selectedDependencyCandidate is not null &&
            !string.IsNullOrWhiteSpace(DependencyArtifactPath.Text);
        DependencyStagingApprovalBox.IsEnabled =
            reviewEnabled && reviewedDependencyArtifact?.RuntimeArchive is not null;
        StageDependencyRuntimeButton.IsEnabled =
            reviewEnabled && reviewedDependencyArtifact?.RuntimeArchive is not null &&
            DependencyStagingApprovalBox.IsChecked == true;
        SetDependencyTransferControlsEnabled(enabled);
    }

    private void SetDependencyTransferControlsEnabled(bool enabled)
    {
        var planAvailable = enabled && !dependencyTransferRunning && !dependencyRouteReviewRunning &&
            dependencyTransferCoordinator is not null &&
            dependencyTransferPlan is not null;
        var routeReviewCurrent = dependencyRedirectReviewer is null ||
            dependencyRedirectReviewSession?.IsCurrent == true;
        var transferAvailable = planAvailable && dependencyTransferPlan!.CapacitySufficient &&
            routeReviewCurrent;
        ReviewDependencyRoutesButton.IsEnabled = enabled && !dependencyTransferRunning &&
            !dependencyRouteReviewRunning && dependencyRedirectReviewer is not null &&
            dependencyTransferPlan is not null;
        CancelDependencyRouteReviewButton.IsEnabled = dependencyRouteReviewRunning;
        DependencyTransferApprovalBox.IsEnabled = transferAvailable;
        StartDependencyTransferButton.IsEnabled =
            transferAvailable && DependencyTransferApprovalBox.IsChecked == true;
        CancelDependencyTransferButton.IsEnabled = dependencyTransferRunning;
        VerifyDependencyTransferButton.IsEnabled = planAvailable;
        RecoverDependencyTransferButton.IsEnabled =
            enabled && !dependencyTransferRunning && dependencyTransferCoordinator is not null;
    }

    private void ResetDependencyArtifactReview(string message)
    {
        reviewedDependencyArtifact = null;
        if (DependencyStagingApprovalBox is not null)
        {
            DependencyStagingApprovalBox.IsChecked = false;
            DependencyStagingApprovalBox.IsEnabled = false;
        }
        if (StageDependencyRuntimeButton is not null) StageDependencyRuntimeButton.IsEnabled = false;
        SetDependencyArtifactResult(message);
    }

    private void BrowseInto(TextBox target, string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = filter,
            Title = title,
        };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private void ResetWhisperReview()
    {
        reviewedWhisperBackend = null;
        if (WhisperApprovalBox is null) return;
        WhisperApprovalBox.IsChecked = false;
        WhisperApprovalBox.IsEnabled = false;
        SaveWhisperBackendButton.IsEnabled = false;
        SetWhisperDetails("Enter four existing local file paths, then review them.");
        SetWhisperLicense("Complete runtime and model license text will appear after review.");
    }

    private void SetWhisperReview(WhisperBackendReview review)
    {
        var binding = review.Binding;
        SetWhisperDetails(
            $"Executable: {System.IO.Path.GetFileName(binding.ExecutablePath)}, {review.ExecutableBytes:N0} bytes, SHA-256 {binding.ExecutableSha256}. " +
            $"Model: {System.IO.Path.GetFileName(binding.ModelPath)}, {review.ModelBytes:N0} bytes, SHA-256 {binding.ModelSha256}. " +
            $"Runtime license SHA-256 {binding.RuntimeLicenseSha256}. Model license SHA-256 {binding.ModelLicenseSha256}. " +
            "Publisher authenticity and transcription accuracy are not established by this review.");
        SetWhisperLicense(
            "WHISPER RUNTIME LICENSE — COMPLETE TEXT\n\n" + review.RuntimeLicenseText +
            "\n\nWHISPER MODEL LICENSE — COMPLETE TEXT\n\n" + review.ModelLicenseText);
    }

    private void SetWhisperDetails(string value)
    {
        WhisperBackendDetails.Text = value;
        AutomationProperties.SetName(WhisperBackendDetails, value);
    }

    private void SetWhisperLicense(string value) => WhisperLicenseText.Text = value;

    private void ResetFfmpegReview()
    {
        reviewedFfmpegBackend = null;
        if (FfmpegApprovalBox is null) return;
        FfmpegApprovalBox.IsChecked = false;
        FfmpegApprovalBox.IsEnabled = false;
        SaveFfmpegBackendButton.IsEnabled = false;
        SetFfmpegDetails("Enter an existing executable and license path, then review them.");
        SetFfmpegLicense("Complete FFmpeg license text will appear after review.");
    }

    private void SetFfmpegReview(FfmpegBackendReview review)
    {
        var binding = review.Binding;
        var signer = binding.AuthenticodeStatus == "Valid"
            ? $"Signer {binding.SignerSubject}. Signer certificate SHA-256 {binding.SignerCertificateSha256}."
            : "No valid publisher signature was established. Explicit approval does not create publisher authenticity.";
        SetFfmpegDetails(
            $"Executable: {System.IO.Path.GetFileName(binding.ExecutablePath)}, {review.ExecutableBytes:N0} bytes, SHA-256 {binding.ExecutableSha256}. " +
            $"License SHA-256 {binding.LicenseSha256}. Windows signature status {binding.AuthenticodeStatus}. {signer}");
        SetFfmpegLicense("FFMPEG LICENSE — COMPLETE TEXT\n\n" + review.LicenseText);
    }

    private void SetFfmpegDetails(string value)
    {
        FfmpegBackendDetails.Text = value;
        AutomationProperties.SetName(FfmpegBackendDetails, value);
    }

    private void SetFfmpegLicense(string value) => FfmpegLicenseText.Text = value;

    private static void SetCurrent(TextBlock target, string capability, ApprovedLocalModelBinding? binding)
    {
        var value = binding is null
            ? $"{capability}: none selected."
            : $"{capability}: {binding.Name}. Digest {binding.Digest}. License digest {binding.LicenseSha256}. Approved {binding.ApprovedUtc:u}.";
        target.Text = value;
        AutomationProperties.SetName(target, value);
    }

    private void SetDetails(string value)
    {
        ModelDetailsText.Text = value;
        AutomationProperties.SetName(ModelDetailsText, value);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private void SetLicense(string value) => ModelLicenseText.Text = value;

    private void SetDependencyCatalogSummary(string value)
    {
        DependencyCatalogSummary.Text = value;
        AutomationProperties.SetName(DependencyCatalogSummary, value);
    }

    private void SetHardwareProfile(string value)
    {
        HardwareProfileSummary.Text = value;
        AutomationProperties.SetName(HardwareProfileSummary, value);
    }

    private void SetDependencyTransferPreflight(string value)
    {
        DependencyTransferPreflight.Text = value;
        AutomationProperties.SetName(DependencyTransferPreflight, value);
    }

    private void SetDependencyTransferStatus(string value)
    {
        DependencyTransferStatus.Text = value;
        AutomationProperties.SetName(DependencyTransferStatus, value);
    }

    private void SetDependencyCandidateDetails(string value)
    {
        DependencyCandidateDetails.Text = value;
        AutomationProperties.SetName(DependencyCandidateDetails, value);
    }

    private void SetDependencyArtifactResult(string value)
    {
        DependencyArtifactResult.Text = value;
        AutomationProperties.SetName(DependencyArtifactResult, value);
    }

    private static string FormatDependencyCandidate(PinnedLocalModelDependencyCandidate candidate)
    {
        var containedReview = candidate.License.RequiresContainedReview
            ? "Archive-contained licenses and notices require separate review after staging."
            : "No archive-contained license review is declared for this candidate.";
        var layers = candidate.ManagedLayers.Count == 0
            ? "Managed layers: none."
            : "Managed layers:\n" + string.Join(
                "\n",
                candidate.ManagedLayers.Select(layer =>
                    $"{layer.MediaType}; {layer.ExpectedBytes:N0} bytes; SHA-256 {layer.Sha256}."));
        return
            $"Candidate {candidate.Id}. {candidate.DisplayName}. Kind {candidate.Kind}.\n" +
            $"Source: {candidate.SourceUri}.\n" +
            $"Expected bytes: {candidate.ExpectedBytes:N0}. SHA-256: {candidate.Sha256}.\n" +
            $"License: {candidate.License.Identifier}. Source: {candidate.License.SourceUri}. " +
            $"Expected bytes: {candidate.License.ExpectedBytes:N0}. SHA-256: {candidate.License.Sha256}.\n" +
            $"{containedReview}\n{layers}\n" +
            "This read-only inventory grants no network, download, extraction, install, pull, execution, legal, approval, or selection authority.";
    }

    private static string FormatHardwareProfile(LocalHardwareProfile profile)
    {
        static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        var graphics = profile.Gpus.Count == 0
            ? "Graphics adapters: none reported by the bounded Windows registry inventory."
            : "Graphics adapters:\n" + string.Join(
                "\n",
                profile.Gpus.Select(gpu =>
                    $"{gpu.Name}. Provider {gpu.Provider ?? "unavailable"}. " +
                    $"Windows driver {gpu.DriverVersion ?? "unavailable"}. " +
                    (gpu.ReportedMemoryBytes is long memory
                        ? $"Registry-reported graphics memory {Number(memory)} bytes from {gpu.MemorySource}."
                        : "Registry-reported graphics memory unavailable.")));
        return
            $"Hardware profile. Captured UTC {profile.CapturedUtc:O}. " +
            $"Operating system {profile.OperatingSystem}. Process architecture {profile.ProcessArchitecture}.\n" +
            $"Processor {profile.CpuName}. {profile.LogicalProcessorCount} logical processors.\n" +
            $"Physical memory {Number(profile.PhysicalMemoryBytes)} bytes. Available memory {Number(profile.AvailableMemoryBytes)} bytes.\n" +
            $"Application-data storage {Number(profile.StorageTotalBytes)} total bytes. {Number(profile.StorageAvailableBytes)} available bytes.\n" +
            $"{graphics}\n" +
            "This profile used native Windows kernel and read-only registry data. No external process or network request ran. Capacity is volatile and must be rechecked before resource-intensive work.";
    }

    private static string FormatDependencyTransferPlan(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectPolicyReview redirectPolicy,
        bool transferActionAvailable,
        LocalModelDependencyRedirectReviewSession? routeReviewSession)
    {
        static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        var available = plan.ObservedAvailableFreeBytes is long availableBytes
            ? $"Observed available storage: {Number(availableBytes)} bytes. " +
              (plan.CapacitySufficient ? "The observation meets the minimum." : "The observation is below the minimum.")
            : "Available storage could not be observed, so transfer capacity is not satisfied.";
        var itemDetails = string.Join(
            "\n",
            plan.Items.Select(item =>
                $"{item.SourceUri}; {Number(item.ExpectedBytes)} bytes; SHA-256 {item.Sha256}; roles {string.Join(", ", item.Roles)}."));
        var redirectDetails = redirectPolicy.Routes.Count == 0
            ? "Approved redirect routes: none. The reviewed policy denies every redirect."
            : string.Join(
                "\n",
                redirectPolicy.Routes.Select((route, index) =>
                    $"Approved redirect route {index + 1}: exact item {route.ItemSourceUri}; {Number(route.ExpectedBytes)} bytes; SHA-256 {route.Sha256}; from exact query-free URI {route.FromUriWithoutQuery}; to exact query-free HTTPS URI {route.TargetUriWithoutQuery}."));
        var redirectPolicyState = redirectPolicy.Routes.Count == 0
            ? "The current policy has no routes; no real provider redirect is authorized."
            : "Only the exact displayed redirect routes are authorized.";
        var sessionState = routeReviewSession is null
            ? "No current in-memory provider-route review exists. Production transfer remains unavailable until the user explicitly starts the HEAD-only route review."
            : $"In-memory provider-route review: {routeReviewSession.RequestCount:N0} HEAD requests; {routeReviewSession.RedirectCount:N0} exact query-free transitions; expires {routeReviewSession.ExpiresAtUtc:u}. The review is not persisted and is not transfer approval.";
        var candidateWord = plan.CandidateIds.Count == 1 ? "candidate" : "candidates";
        var itemWord = plan.Items.Count == 1 ? "item" : "items";
        return
            $"Transfer preflight only. Plan SHA-256 {plan.PlanSha256}. " +
            $"{plan.CandidateIds.Count} {candidateWord}. {plan.Items.Count} immutable transfer {itemWord}. " +
            $"{Number(plan.TransferBytes)} network bytes. {Number(plan.MinimumFreeBytes)} minimum free bytes. " +
            "The minimum reserves one temporary transfer copy, one retained verified copy, and 536,870,912 workspace bytes. " +
            $"{available} Capacity must be rechecked immediately before any future transfer.\n" +
            $"{itemDetails}\n" +
            $"Redirect policy SHA-256 {redirectPolicy.PolicySha256}. {redirectDetails}\n" +
            $"{sessionState}\n" +
            "The inert core transport reloads the authenticated catalog, requires exact-item approval, disables automatic redirects, proxy, credentials, preauthentication, cookies and decompression, and rolls back any partial stream unless the requested URI, final status, length and SHA-256 all match. Its bounded manual redirect policy is bound to the displayed digest and routes. " +
            $"{redirectPolicyState} Its private set coordinator serializes transfer, verification and cleanup; rechecks capacity; uses random incoming storage; protects a source-path-free receipt; atomically publishes only a complete plan; and can rehash the completed set. " +
            (transferActionAvailable
                ? "This window exposes one separately approved exact-plan transfer, cancellation, protected verification and abandoned-partial recovery. " +
                  (redirectPolicy.Routes.Count == 0
                      ? "Current provider redirects remain denied."
                      : "Only the displayed redirect routes can be followed.")
                : "These boundaries are not connected to a transfer action in this validation window.") + "\n" +
            "No transfer approval exists. Nothing was downloaded, installed, extracted, pulled, registered, selected, or executed.";
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsExactModel(LocalModelDescriptor? expected, LocalModelDescriptor actual) =>
        expected is not null &&
        string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
        string.Equals(expected.Digest, actual.Digest, StringComparison.Ordinal) &&
        expected.SizeBytes == actual.SizeBytes;

    private sealed record CandidateRow(
        LocalModelDescriptor Descriptor,
        bool EligibleVision,
        bool EligibleEmbedding)
    {
        public string Display =>
            $"{Descriptor.Name}. Digest {Descriptor.Digest}. " +
            $"{Descriptor.SizeBytes:N0} bytes. " +
            $"Capabilities: {(Descriptor.SupportsVision ? "vision " : string.Empty)}" +
            $"{(Descriptor.SupportsEmbeddings ? "embedding " : string.Empty)}" +
            $"{(EligibleVision || EligibleEmbedding ? "Eligible." : "Not eligible.")}";

        public override string ToString() => Display;
    }

    private sealed record DependencyCandidateRow(PinnedLocalModelDependencyCandidate Candidate)
    {
        public string Display =>
            $"{Candidate.DisplayName}. {Candidate.ExpectedBytes:N0} bytes. SHA-256 {Candidate.Sha256}.";

        public override string ToString() => Display;
    }
}
