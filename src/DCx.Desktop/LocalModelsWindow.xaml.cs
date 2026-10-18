using System.Security.Cryptography;
using System.Text;
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
    private readonly string applicationDataRoot;
    private readonly LocalModelDependencyCatalogReview? dependencyCatalogFixture;
    private readonly CancellationTokenSource lifetime = new();
    private CandidateRow? reviewedCandidate;
    private string? reviewedLicenseSha256;
    private WhisperBackendReview? reviewedWhisperBackend;
    private FfmpegBackendReview? reviewedFfmpegBackend;
    private DependencyCandidateRow? selectedDependencyCandidate;
    private LocalModelDependencyArtifactReview? reviewedDependencyArtifact;

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
            stageAnchorStore: null)
    {
    }

    internal LocalModelsWindow(
        LocalModelSelectionStore store,
        WhisperBackendSelectionStore whisperStore,
        FfmpegBackendSelectionStore ffmpegStore,
        string applicationDataRoot,
        LocalModelDependencyCatalogReview? dependencyCatalogFixture,
        TimeProvider? timeProvider,
        IWhisperRuntimeStageAnchorStore? stageAnchorStore)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.whisperStore = whisperStore ?? throw new ArgumentNullException(nameof(whisperStore));
        this.ffmpegStore = ffmpegStore ?? throw new ArgumentNullException(nameof(ffmpegStore));
        this.applicationDataRoot = applicationDataRoot ?? throw new ArgumentNullException(nameof(applicationDataRoot));
        this.dependencyCatalogFixture = dependencyCatalogFixture;
        whisperRuntimeStager = new WhisperRuntimeStager(timeProvider, stageAnchorStore);
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await LoadDependencyCatalogAsync();
            await RefreshAsync();
        };
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            DependencyCandidatesList.ItemsSource = rows;
            SetDependencyCatalogSummary(dependencyCatalogFixture is null
                ? $"Catalog {catalog.CatalogId}. SHA-256 {catalog.CatalogSha256}. " +
                  $"{rows.Length} immutable candidates. Network, download, install, pull, execution, registration, and selection actions are absent; only a separately approved exact Whisper runtime can be staged inertly."
                : $"Synthetic validation catalog {catalog.CatalogId}. SHA-256 {catalog.CatalogSha256}. " +
                  $"{rows.Length} generated candidate. Network, download, install, pull, execution, registration, and selection are unavailable.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            DependencyCandidatesList.ItemsSource = null;
            SetDependencyCatalogSummary(
                "The built-in dependency catalog failed authentication and no candidate is available. No external action occurred.");
        }
        finally
        {
            SetDependencyControlsEnabled(true);
        }
    }

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
            var counts = models
                .GroupBy(model => model.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var rows = models
                .OrderBy(model => model.Name, StringComparer.Ordinal)
                .ThenBy(model => model.Digest, StringComparer.Ordinal)
                .Select(model => new CandidateRow(
                    model,
                    counts[model.Name] == 1 && model.HasDigestBoundName && model.SupportsVision,
                    counts[model.Name] == 1 && model.HasDigestBoundName && model.SupportsEmbeddings))
                .ToArray();
            ModelsList.ItemsSource = rows;
            await UpdateCurrentSelectionsAsync();
            var eligible = rows.Count(row => row.EligibleVision || row.EligibleEmbedding);
            SetStatus(
                $"Found {rows.Length} installed model records. {eligible} unique digest-bound records are eligible. Nothing was downloaded or uploaded.");
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
        SetCurrent(CurrentVisionText, "Vision", state.Vision);
        SetCurrent(CurrentEmbeddingText, "Embedding", state.Embedding);
    }

    private async Task UpdateWhisperSelectionAsync()
    {
        var state = await whisperStore.GetStateAsync(lifetime.Token);
        var binding = state.Backend;
        var value = binding is null
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
        var value = binding is null
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
        DependencyCandidatesList.IsEnabled = enabled;
        DependencyArtifactPath.IsEnabled = enabled && selectedDependencyCandidate is not null;
        BrowseDependencyArtifactButton.IsEnabled = enabled && selectedDependencyCandidate is not null;
        ReviewDependencyArtifactButton.IsEnabled =
            enabled && selectedDependencyCandidate is not null &&
            !string.IsNullOrWhiteSpace(DependencyArtifactPath.Text);
        DependencyStagingApprovalBox.IsEnabled =
            enabled && reviewedDependencyArtifact?.RuntimeArchive is not null;
        StageDependencyRuntimeButton.IsEnabled =
            enabled && reviewedDependencyArtifact?.RuntimeArchive is not null &&
            DependencyStagingApprovalBox.IsChecked == true;
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

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
