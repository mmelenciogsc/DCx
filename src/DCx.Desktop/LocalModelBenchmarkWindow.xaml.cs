using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DCx.Core.LocalModels;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class LocalModelBenchmarkWindow : Window
{
    private readonly WhisperBackendSelectionStore whisperStore;
    private readonly string applicationDataRoot;
    private readonly CancellationTokenSource lifetime = new();
    private ILocalModelBenchmarkManifestReview? reviewedManifest;
    private ApprovedWhisperBackendBinding? reviewedWhisperBinding;
    private string? reviewedBackendSha256;
    private DateTimeOffset reviewedAtUtc;
    private CancellationTokenSource? activeRun;
    private bool busy;

    public LocalModelBenchmarkWindow(
        WhisperBackendSelectionStore whisperStore,
        string applicationDataRoot)
    {
        this.whisperStore = whisperStore ?? throw new ArgumentNullException(nameof(whisperStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        this.applicationDataRoot = Path.GetFullPath(applicationDataRoot);
        InitializeComponent();
        Loaded += async (_, _) => await UpdateBackendAvailabilityAsync();
        Closed += (_, _) =>
        {
            activeRun?.Cancel();
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private void OnBrowseManifestClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "DCx benchmark manifests (*.json)|*.json",
            Title = "Select a local model benchmark manifest",
        };
        if (dialog.ShowDialog(this) == true) ManifestPathBox.Text = dialog.FileName;
    }

    private void OnManifestPathChanged(object sender, TextChangedEventArgs e)
    {
        if (ApprovalBox is null) return;
        ResetReview("The manifest path changed. Review the exact manifest again before approving a run.");
    }

    private async void OnReviewManifestClick(object sender, RoutedEventArgs e)
    {
        ResetReview("Reviewing the local manifest and fixture suite.");
        SetBusy(true, running: false);
        SetStatus("Reviewing and hashing the bounded local manifest and fixture suite. No model is running.");
        try
        {
            var review = await LocalModelBenchmarkManifestLoader
                .LoadAsync(ManifestPathBox.Text, lifetime.Token);
            reviewedManifest = review;
            reviewedAtUtc = DateTimeOffset.UtcNow;
            if (review is TranscriptionBenchmarkManifestReview transcription)
            {
                var binding = (await whisperStore.GetStateAsync(lifetime.Token)).Backend;
                if (binding is null)
                {
                    SetReviewDetails(DescribeTranscriptionReview(transcription, "No approved Whisper backend is registered."));
                    SetStatus("The exact transcription suite was reviewed, but running is unavailable until an already-installed Whisper backend is explicitly registered.");
                    return;
                }

                var backend = new WhisperTranscriptionBenchmarkBackend(binding, BenchmarkTimeout(transcription));
                reviewedWhisperBinding = binding;
                reviewedBackendSha256 = backend.BindingSha256;
                SetReviewDetails(DescribeTranscriptionReview(
                    transcription,
                    $"Approved transcription backend binding SHA-256: {backend.BindingSha256}."));
                ApprovalBox.IsEnabled = true;
                SetStatus("Exact transcription backend, suite, policy, thresholds, provenance, and license are ready for review. Approval remains unchecked.");
            }
            else if (review is EmbeddingBenchmarkManifestReview embedding)
            {
                SetReviewDetails(DescribeEmbeddingReview(embedding));
                SetStatus("The exact embedding suite was reviewed. Running is unavailable because no approved production embedding benchmark adapter and resource probe is implemented yet.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ResetReview("The manifest or fixture suite could not be reviewed safely.");
            SetStatus("Benchmark review failed closed. Confirm the strict schema, bounded local files, provenance, license, thresholds, and resource limits.");
        }
        finally
        {
            SetBusy(false, running: false);
        }
    }

    private void OnApprovalChanged(object sender, RoutedEventArgs e) => UpdateRunAvailability();

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (ApprovalBox.IsChecked != true ||
            reviewedManifest is not TranscriptionBenchmarkManifestReview reviewed ||
            reviewedWhisperBinding is null || reviewedBackendSha256 is null)
        {
            SetStatus("No benchmark ran. Review an available backend and exact manifest, then check the explicit approval box.");
            ApprovalBox.Focus();
            return;
        }

        activeRun = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true, running: true);
        SetResult("Benchmark running. No evidence has been saved yet.");
        SetStatus("Running the explicitly approved local transcription benchmark. Cancel remains available.");
        try
        {
            var current = Assert.IsTranscription(await LocalModelBenchmarkManifestLoader
                .LoadAsync(ManifestPathBox.Text, activeRun.Token));
            RequireSameReview(reviewed, current);
            var currentBinding = (await whisperStore.GetStateAsync(activeRun.Token)).Backend;
            if (currentBinding is null || !reviewedWhisperBinding.HasSameIdentity(currentBinding))
            {
                throw new InvalidOperationException("The approved transcription backend changed after review.");
            }
            var backend = new WhisperTranscriptionBenchmarkBackend(currentBinding, BenchmarkTimeout(current));
            if (!string.Equals(backend.BindingSha256, reviewedBackendSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The exact benchmark backend binding changed after review.");
            }

            var approval = new LocalModelBenchmarkApproval(
                LocalModelBenchmarkKind.Transcription,
                reviewedBackendSha256,
                reviewed.FixtureSuiteSha256,
                reviewed.PolicySha256,
                reviewedAtUtc,
                reviewedAtUtc.AddMinutes(20));
            var evidence = await LocalModelBenchmarkRunner.RunTranscriptionAsync(
                approval,
                explicitlyApproved: true,
                current.Fixtures,
                current.Limits,
                backend,
                activeRun.Token);
            var evidenceStore = await LocalModelBenchmarkEvidenceStore.OpenAsync(
                applicationDataRoot,
                activeRun.Token);
            await evidenceStore.SaveTranscriptionAsync(evidence, activeRun.Token);
            SetResult(DescribeEvidence(evidence));
            ResultText.Focus();
            SetStatus(evidence.Result.Passed
                ? "Benchmark passed. Aggregate-only evidence replaced the previous local transcription benchmark evidence. The production selection was not changed."
                : "Benchmark failed its reviewed thresholds. Aggregate-only evidence was saved, and the production selection was not changed.");
        }
        catch (OperationCanceledException) when (activeRun?.IsCancellationRequested == true)
        {
            SetResult("Benchmark canceled. No result from this run was saved.");
            ResultText.Focus();
            SetStatus("The local benchmark was canceled. Approval was revoked and no partial result was retained.");
        }
        catch
        {
            SetResult("Benchmark failed closed. No result from this run was saved.");
            ResultText.Focus();
            SetStatus("The backend, manifest, fixture suite, or policy could not be revalidated or executed safely. Review everything again.");
        }
        finally
        {
            ApprovalBox.IsChecked = false;
            reviewedManifest = null;
            reviewedWhisperBinding = null;
            reviewedBackendSha256 = null;
            activeRun?.Dispose();
            activeRun = null;
            SetBusy(false, running: false);
            ApprovalBox.IsEnabled = false;
            RunButton.IsEnabled = false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        activeRun?.Cancel();
        SetStatus("Canceling the active local benchmark.");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task UpdateBackendAvailabilityAsync()
    {
        try
        {
            var binding = (await whisperStore.GetStateAsync(lifetime.Token)).Backend;
            SetBackend(binding is null
                ? "Transcription benchmark unavailable: no explicitly approved local Whisper backend is registered. Embedding benchmark unavailable: no approved production adapter and resource probe is implemented."
                : $"Transcription benchmark available through the approved {Path.GetFileName(binding.ExecutablePath)} and {Path.GetFileName(binding.ModelPath)} binding. Embedding benchmark unavailable: no approved production adapter and resource probe is implemented.");
            SetStatus("Choose a strict local benchmark manifest to begin exact review. Approval is unchecked by default.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetBackend("Local benchmark backend availability could not be verified. Runs are unavailable.");
            SetStatus("Backend availability failed closed. No benchmark can run.");
            ReviewManifestButton.IsEnabled = false;
        }
    }

    private void ResetReview(string details)
    {
        reviewedManifest = null;
        reviewedWhisperBinding = null;
        reviewedBackendSha256 = null;
        reviewedAtUtc = default;
        ApprovalBox.IsChecked = false;
        ApprovalBox.IsEnabled = false;
        RunButton.IsEnabled = false;
        SetReviewDetails(details);
    }

    private void SetBusy(bool value, bool running)
    {
        busy = value;
        ManifestPathBox.IsEnabled = !value;
        BrowseManifestButton.IsEnabled = !value;
        ReviewManifestButton.IsEnabled = !value;
        ApprovalBox.IsEnabled = !value && reviewedWhisperBinding is not null;
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = value && running;
        if (!value) UpdateRunAvailability();
    }

    private void UpdateRunAvailability() =>
        RunButton.IsEnabled = !busy && reviewedManifest is TranscriptionBenchmarkManifestReview &&
                              reviewedWhisperBinding is not null && ApprovalBox.IsChecked == true;

    private static void RequireSameReview(
        TranscriptionBenchmarkManifestReview expected,
        TranscriptionBenchmarkManifestReview actual)
    {
        if (!string.Equals(expected.ManifestSha256, actual.ManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(expected.FixtureSuiteSha256, actual.FixtureSuiteSha256, StringComparison.Ordinal) ||
            !string.Equals(expected.PolicySha256, actual.PolicySha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The exact reviewed benchmark manifest, suite, or policy changed.");
        }
    }

    private static TimeSpan BenchmarkTimeout(TranscriptionBenchmarkManifestReview review)
    {
        var milliseconds = Math.Clamp(
            review.Limits.Resources.MaximumElapsed.TotalMilliseconds,
            100,
            TimeSpan.FromMinutes(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string DescribeTranscriptionReview(
        TranscriptionBenchmarkManifestReview review,
        string backend) =>
        $"Name: {review.Name}.{Environment.NewLine}" +
        $"Type: transcription. Fixtures: {review.Fixtures.Count}.{Environment.NewLine}" +
        $"Provenance: {review.Provenance.Source}. License: {review.Provenance.License}.{Environment.NewLine}" +
        $"Manifest SHA-256: {review.ManifestSha256}.{Environment.NewLine}" +
        $"Fixture-suite SHA-256: {review.FixtureSuiteSha256}.{Environment.NewLine}" +
        $"Policy SHA-256: {review.PolicySha256}.{Environment.NewLine}" +
        $"Thresholds: maximum WER {Percent(review.Limits.MaximumWordErrorRate)}; maximum CER {Percent(review.Limits.MaximumCharacterErrorRate)}; " +
        $"maximum elapsed {review.Limits.Resources.MaximumElapsed.TotalMilliseconds:N0} milliseconds; " +
        $"maximum working set {review.Limits.Resources.MaximumPeakWorkingSetBytes:N0} bytes; " +
        $"maximum GPU {(review.Limits.Resources.MaximumPeakGpuBytes is long gpu ? $"{gpu:N0} bytes" : "not required")}; unload confirmation required.{Environment.NewLine}" +
        backend;

    private static string DescribeEmbeddingReview(EmbeddingBenchmarkManifestReview review) =>
        $"Name: {review.Name}.{Environment.NewLine}" +
        $"Type: embedding retrieval. Documents: {review.Documents.Count}. Queries: {review.Queries.Count}.{Environment.NewLine}" +
        $"Provenance: {review.Provenance.Source}. License: {review.Provenance.License}.{Environment.NewLine}" +
        $"Manifest SHA-256: {review.ManifestSha256}.{Environment.NewLine}" +
        $"Fixture-suite SHA-256: {review.FixtureSuiteSha256}.{Environment.NewLine}" +
        $"Policy SHA-256: {review.PolicySha256}.{Environment.NewLine}" +
        $"Thresholds: K {review.Limits.K}; minimum recall {Percent(review.Limits.MinimumRecallAtK)}; " +
        $"minimum mean reciprocal rank {Percent(review.Limits.MinimumMeanReciprocalRank)}.{Environment.NewLine}" +
        "Unavailable: no approved production embedding benchmark adapter and resource probe is implemented.";

    private static string DescribeEvidence(TranscriptionBenchmarkEvidence evidence)
    {
        var result = evidence.Result;
        var failures = result.FailureCodes.Count == 0
            ? "none"
            : string.Join(", ", result.FailureCodes);
        return $"{(result.Passed ? "PASS" : "FAIL")}.{Environment.NewLine}" +
               $"Fixtures: {result.FixtureCount}. Reference words: {result.ReferenceWordCount}.{Environment.NewLine}" +
               $"WER: {Percent(result.WordErrorRate)}. Substitutions: {result.Substitutions}. Deletions: {result.Deletions}. Insertions: {result.Insertions}.{Environment.NewLine}" +
               $"CER: {Percent(result.CharacterErrorRate)}. Character errors: {result.CharacterErrors}.{Environment.NewLine}" +
               $"Elapsed: {result.Resources.ElapsedMilliseconds:N0} milliseconds. Peak working set: {result.Resources.PeakWorkingSetBytes?.ToString("N0", CultureInfo.CurrentCulture) ?? "missing"} bytes. " +
               $"Peak GPU: {result.Resources.PeakGpuBytes?.ToString("N0", CultureInfo.CurrentCulture) ?? "not measured"} bytes. Unload confirmed: {result.Resources.UnloadConfirmed}.{Environment.NewLine}" +
               $"Failure codes: {failures}.{Environment.NewLine}" +
               $"Approval SHA-256: {evidence.ApprovalSha256}. Aggregate-only evidence schema: {evidence.SchemaVersion}.";
    }

    private static string Percent(double value) => value.ToString("P2", CultureInfo.CurrentCulture);

    private void SetBackend(string value)
    {
        BackendText.Text = value;
        AutomationProperties.SetName(BackendText, value);
    }

    private void SetReviewDetails(string value)
    {
        ReviewDetailsText.Text = value;
        AutomationProperties.SetName(ReviewDetailsText, value);
    }

    private void SetResult(string value)
    {
        ResultText.Text = value;
        AutomationProperties.SetName(ResultText, value);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private static class Assert
    {
        public static TranscriptionBenchmarkManifestReview IsTranscription(
            ILocalModelBenchmarkManifestReview review) =>
            review as TranscriptionBenchmarkManifestReview
            ?? throw new InvalidOperationException("The reviewed benchmark kind changed.");
    }
}
