using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DCx.Core.Contributions;
using DCx.Core.Knowledge;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class ContributionsWindow : Window
{
    private readonly ContributionScanner scanner = new();
    private readonly ContributionPreparationService preparationService;
    private readonly ContributionArtifactSelectionService artifactSelectionService = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly string candidateId = $"candidate:{Guid.NewGuid():N}";
    private string? reviewedDigest;
    private string? approvedDigest;
    private string? approvalNonce;
    private DateTimeOffset approvalTime;
    private PreparedContribution? preparedContribution;
    private ContributionSimulationStatus? simulationStatus;
    private Uri? simulationOriginBinding;
    private CancellationTokenSource? requestCancellation;
    private bool requestInProgress;
    private bool submissionMayExist;
    private bool artifactLoadInProgress;
    private bool windowClosed;
    private SelectedContributionArtifact? selectedArtifact;

    public ContributionsWindow()
    {
        preparationService = new ContributionPreparationService(scanner);
        InitializeComponent();
        LicenseBox.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OnCandidateChanged),
            handledEventsToo: true);
        Closed += (_, _) =>
        {
            windowClosed = true;
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            requestCancellation = null;
            lifetime.Cancel();
            lifetime.Dispose();
            selectedArtifact?.Dispose();
            selectedArtifact = null;
        };
    }

    private void OnCandidateChanged(object sender, RoutedEventArgs e)
        => InvalidateCandidateState();

    private void InvalidateCandidateState()
    {
        reviewedDigest = null;
        approvedDigest = null;
        approvalNonce = null;
        preparedContribution = null;
        simulationStatus = null;
        simulationOriginBinding = null;
        submissionMayExist = false;
        if (ItemApprovalBox is not null)
        {
            ItemApprovalBox.IsChecked = false;
        }

        if (FindingsList is not null)
        {
            FindingsList.ItemsSource = null;
        }

        ClearNetworkApprovals();

        UpdateGate();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContributionKindBox is null) return;
        ReleaseSelectedArtifact(IsCcbSelected()
            ? "No CCB package is selected."
            : "CKB knowledge items do not use an artifact package.");
        InvalidateCandidateState();
    }

    private async void OnBrowseArtifactClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a DCx CCB contribution package",
            Filter = "DCx CCB package (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        ArtifactPathBox.Text = dialog.FileName;
        await LoadArtifactAsync();
    }

    private async void OnLoadArtifactClick(object sender, RoutedEventArgs e) =>
        await LoadArtifactAsync();

    private void OnClearArtifactClick(object sender, RoutedEventArgs e)
    {
        ArtifactPathBox.Clear();
        ReleaseSelectedArtifact("The selected CCB package was cleared from memory.");
        InvalidateCandidateState();
        ContributionStatusText.Text = "CCB package cleared. Select and review a package before approving this item.";
    }

    private async Task LoadArtifactAsync()
    {
        if (!IsCcbSelected())
        {
            ArtifactPathBox.Clear();
            ContributionStatusText.Text = "Artifact loading is available only for a CCB tool package.";
            return;
        }

        var path = ArtifactPathBox.Text;
        ReleaseSelectedArtifact("Loading and validating the selected CCB package locally.");
        InvalidateCandidateState();
        artifactLoadInProgress = true;
        UpdateGate();
        try
        {
            var selection = await artifactSelectionService.LoadCcbAsync(path, lifetime.Token);
            if (windowClosed)
            {
                selection.Dispose();
                return;
            }
            selectedArtifact = selection;
            ResourceIdBox.Text = selection.Manifest.Id;
            VersionBox.Text = selection.Manifest.Version;
            LicenseBox.Text = selection.DeclaredLicenses.FirstOrDefault() ?? string.Empty;
            ArtifactReviewBox.Text = selection.ReviewText;
            ArtifactStatusText.Text =
                $"Loaded {selection.FileName}. {selection.SizeBytes} bytes. SHA-256 {selection.Sha256}. Manifest {selection.Manifest.Id} version {selection.Manifest.Version}. The path was cleared.";
            ContributionStatusText.Text =
                "The CCB package passed local structural admission. Review its complete text and scanner findings before approval. No network request occurred.";
            InvalidateCandidateState();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (ContributionArtifactException exception)
        {
            ArtifactStatusText.Text = $"CCB package blocked: {exception.Code}. The path was cleared.";
            ContributionStatusText.Text =
                $"CCB package blocked: {exception.Code}. No package bytes were retained or sent.";
        }
        catch
        {
            ArtifactStatusText.Text = "CCB package loading failed safely. The path was cleared.";
            ContributionStatusText.Text = "CCB package loading failed safely. No path or package bytes were logged or sent.";
        }
        finally
        {
            ArtifactPathBox.Clear();
            artifactLoadInProgress = false;
            UpdateGate();
        }
    }

    private void ReleaseSelectedArtifact(string status)
    {
        selectedArtifact?.Dispose();
        selectedArtifact = null;
        if (ArtifactReviewBox is not null) ArtifactReviewBox.Clear();
        if (ArtifactStatusText is not null) ArtifactStatusText.Text = status;
    }

    private bool IsCcbSelected() => ContributionKindBox?.SelectedIndex == 1;

    private SelectedContributionArtifact? ValidateArtifactBinding(ContributionCandidate candidate)
    {
        if (candidate.Kind == KnowledgeResourceKind.Ckb) return null;
        var selection = selectedArtifact ??
            throw new ContributionArtifactException("ccb_artifact_required");
        if (!string.Equals(selection.Manifest.Id, candidate.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(selection.Manifest.Version, candidate.Version, StringComparison.Ordinal) ||
            !string.Equals(selection.Sha256, candidate.ArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
            !selection.DeclaredLicenses.Contains(candidate.License, StringComparer.OrdinalIgnoreCase) ||
            !selection.LicenseText.Contains(candidate.License, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContributionArtifactException("ccb_artifact_metadata_mismatch");
        }

        return selection;
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();
            var artifact = ValidateArtifactBinding(candidate);
            var scan = scanner.Scan(artifact is null
                ? candidate
                : candidate with { Body = $"{candidate.Body}\n{artifact.ReviewText}" });
            reviewedDigest = ContributionCodec.CandidateSha256(candidate);
            FindingsList.ItemsSource = scan.Findings.Count == 0
                ? ["No automated findings. Human review is still required."]
                : scan.Findings.Select(item => $"{item.Severity}: {item.Code}").ToArray();
            ContributionStatusText.Text = scan.IsEligible
                ? "Automated scan found no blocking issue. Review and approve this exact item if appropriate."
                : "Findings require quarantine review. Values are not echoed into this list.";
        }
        catch (ContributionArtifactException exception)
        {
            ContributionStatusText.Text = $"The CCB package cannot be reviewed: {exception.Code}. Nothing was sent.";
        }
        catch
        {
            ContributionStatusText.Text = "The item cannot be reviewed safely. No values were logged or sent.";
        }

        UpdateGate();
    }

    private void OnItemApprovalChecked(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();
            _ = ValidateArtifactBinding(candidate);
            var digest = ContributionCodec.CandidateSha256(candidate);
            if (!string.Equals(digest, reviewedDigest, StringComparison.Ordinal))
            {
                ItemApprovalBox.IsChecked = false;
                ContributionStatusText.Text = "Review the current item before approving it.";
            }
            else
            {
                approvedDigest = digest;
                approvalNonce = $"approval-{Guid.NewGuid():N}";
                approvalTime = DateTimeOffset.UtcNow;
            }
        }
        catch (ContributionArtifactException exception)
        {
            ItemApprovalBox.IsChecked = false;
            ContributionStatusText.Text =
                $"The CCB package cannot be approved: {exception.Code}. Review the exact package again.";
        }
        catch
        {
            ItemApprovalBox.IsChecked = false;
            ContributionStatusText.Text = "The item cannot be approved safely. Review the current item again.";
        }

        UpdateGate();
    }

    private void OnGateChanged(object sender, RoutedEventArgs e) => UpdateGate();

    private void OnPrepareClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();
            _ = ValidateArtifactBinding(candidate);
            preparedContribution = preparationService.Prepare(
                new ContributionPreferences(PreparationEnabledBox.IsChecked == true),
                candidate,
                new ContributionAttestation(
                    RightsBox.IsChecked == true,
                    LicenseConfirmedBox.IsChecked == true,
                    SensitiveReviewedBox.IsChecked == true),
                new ContributionItemApproval(candidateId, approvedDigest!, approvalNonce!, approvalTime),
                DateTimeOffset.UtcNow);
            simulationStatus = null;
            simulationOriginBinding = null;
            submissionMayExist = false;
            ContributionStatusText.Text =
                $"Prepared locally as {preparedContribution.State}. Findings: {preparedContribution.Scan.Findings.Count}. No upload or publication occurred. Optional loopback submission now requires a separate approval.";
            ClearNetworkApprovals();
            ItemApprovalBox.IsChecked = false;
            approvedDigest = null;
            approvalNonce = null;
            PrepareButton.IsEnabled = false;
        }
        catch (ContributionPolicyException exception)
        {
            ContributionStatusText.Text = $"Preparation blocked: {exception.Code}. Nothing was uploaded.";
        }
        catch (ContributionArtifactException exception)
        {
            ContributionStatusText.Text = $"Preparation blocked: {exception.Code}. Nothing was uploaded.";
        }
        catch
        {
            ContributionStatusText.Text = "Preparation failed safely. Nothing was uploaded.";
        }

        UpdateGate();
    }

    private void OnSubmissionSettingsChanged(object sender, RoutedEventArgs e)
    {
        ClearNetworkApprovals();
        if (SimulationEndpointBox is not null && ReferenceEquals(sender, SimulationEndpointBox))
        {
            simulationStatus = null;
        }

        UpdateGate();
    }

    private void OnSubmissionApprovalChanged(object sender, RoutedEventArgs e) => UpdateGate();

    private async void OnSubmitSimulationClick(object sender, RoutedEventArgs e)
    {
        if (preparedContribution is null || SubmissionApprovalBox.IsChecked != true ||
            requestInProgress || submissionMayExist)
        {
            ContributionStatusText.Text = submissionMayExist
                ? "Submission blocked. Query the original loopback simulation before any retry."
                : "Submission blocked. Prepare the exact item and approve this separate send first.";
            return;
        }

        SelectedContributionArtifact? artifact;
        try
        {
            artifact = ValidateArtifactBinding(preparedContribution.Candidate);
        }
        catch (ContributionArtifactException exception)
        {
            ClearNetworkApprovals();
            ContributionStatusText.Text =
                $"Submission blocked: {exception.Code}. Reselect, review, and prepare the exact CCB package.";
            UpdateGate();
            return;
        }

        if (!TryStartRequest(out var endpoint))
        {
            return;
        }

        simulationStatus = null;
        ContributionStatusText.Text = "Sending the exact prepared item to the loopback simulation for quarantine. No publication is authorized.";
        try
        {
            using var client = new ContributionSubmissionClient(endpoint, TimeSpan.FromSeconds(30));
            simulationOriginBinding = endpoint;
            submissionMayExist = true;
            var result = await client.SubmitAsync(
                preparedContribution,
                ContributorTokenBox.Password,
                artifact?.Content ?? default,
                cancellationToken: requestCancellation!.Token);
            ReleaseSelectedArtifactIfPresent(
                "The exact CCB package was accepted into server quarantine. Its in-memory bytes and review text were cleared.");
            ContributionStatusText.Text =
                $"The local simulation accepted {result.SubmissionId} into {result.State}. No approval or publication occurred. Query status before any retry. The token was not saved.";
        }
        catch (OperationCanceledException)
        {
            ContributionStatusText.Text = "Local simulation submission canceled. Acceptance is unknown; check the server before retrying.";
        }
        catch (ContributionSubmissionException exception)
        {
            if (exception.Code is "contributor_token_invalid" or "submission_envelope_invalid")
            {
                submissionMayExist = false;
                simulationOriginBinding = null;
            }
            ContributionStatusText.Text = submissionMayExist
                ? $"Local simulation submission blocked: {exception.Code}. Acceptance is unknown; query status before retrying. The token was not saved."
                : $"Local simulation submission blocked: {exception.Code}. Nothing was sent. The token was not saved.";
        }
        catch (ArgumentException)
        {
            submissionMayExist = false;
            simulationOriginBinding = null;
            ContributionStatusText.Text = "Submission blocked: simulation_origin_invalid. Nothing was sent.";
        }
        catch
        {
            ContributionStatusText.Text = "Local simulation submission failed safely. Acceptance is unknown; query status before retrying. The token was not saved or logged.";
        }
        finally
        {
            CompleteRequest();
        }
    }

    private async void OnQuerySimulationStatusClick(object sender, RoutedEventArgs e)
    {
        if (preparedContribution is null || StatusApprovalBox.IsChecked != true || requestInProgress)
        {
            ContributionStatusText.Text = "Status query blocked. Prepare the exact item and approve this separate query first.";
            return;
        }

        if (!TryStartRequest(out var endpoint)) return;
        simulationStatus = null;
        WithdrawalApprovalBox.IsChecked = false;
        UpdateGate();
        ContributionStatusText.Text = "Querying minimized status for the exact prepared item from the loopback simulation.";
        try
        {
            using var client = new ContributionSubmissionClient(endpoint, TimeSpan.FromSeconds(30));
            var status = await client.GetStatusAsync(
                preparedContribution,
                ContributorTokenBox.Password,
                requestCancellation!.Token);
            simulationStatus = status;
            simulationOriginBinding = endpoint;
            submissionMayExist = true;
            ReleaseSelectedArtifactIfPresent(
                "The server confirmed the exact CCB package. Its local in-memory bytes and review text were cleared.");
            ContributionStatusText.Text = status.State == ContributionState.Approved
                ? $"The exact item is {status.State} at release {status.ReleaseSequence}. Withdrawal requires a separate approval. No contribution body was returned."
                : $"The exact item is {status.State}. No contribution body was returned. No new approval or publication occurred.";
        }
        catch (OperationCanceledException)
        {
            ContributionStatusText.Text = "Status query canceled. The last known state was cleared; query again before another action.";
        }
        catch (ContributionSubmissionException exception)
        {
            if (exception.Code == "submission_missing")
            {
                submissionMayExist = false;
                simulationOriginBinding = null;
                if (preparedContribution?.Candidate.Kind == KnowledgeResourceKind.Ccb && selectedArtifact is null)
                {
                    preparedContribution = null;
                    reviewedDigest = null;
                    approvedDigest = null;
                    approvalNonce = null;
                    ContributionStatusText.Text =
                        "The loopback simulation reports submission_missing. The cleared CCB package must be reselected, reviewed, and prepared before a new approved send.";
                }
                else
                {
                    ContributionStatusText.Text =
                        "The loopback simulation reports submission_missing. A new send still requires explicit approval.";
                }
            }
            else
            {
                ContributionStatusText.Text = $"Status query blocked: {exception.Code}. The last known state was cleared.";
            }
        }
        catch (ArgumentException)
        {
            ContributionStatusText.Text = "Status query blocked: simulation_origin_invalid. Nothing was sent.";
        }
        catch
        {
            ContributionStatusText.Text = "Status query failed safely. The last known state was cleared and the token was not saved or logged.";
        }
        finally
        {
            CompleteRequest();
        }
    }

    private async void OnWithdrawSimulationClick(object sender, RoutedEventArgs e)
    {
        if (preparedContribution is null || simulationStatus?.State != ContributionState.Approved ||
            WithdrawalApprovalBox.IsChecked != true || requestInProgress)
        {
            ContributionStatusText.Text = "Withdrawal blocked. Query an Approved state and approve this separate withdrawal first.";
            return;
        }

        if (!TryStartRequest(out var endpoint)) return;
        var approvedStatus = simulationStatus!;
        simulationStatus = null;
        UpdateGate();
        ContributionStatusText.Text = "Withdrawing the exact approved item from the loopback simulation. This creates a local simulation deprecation only.";
        try
        {
            using var client = new ContributionSubmissionClient(endpoint, TimeSpan.FromSeconds(30));
            var result = await client.WithdrawAsync(
                preparedContribution,
                ContributorTokenBox.Password,
                requestCancellation!.Token);
            simulationStatus = approvedStatus with
            {
                State = result.State,
                ReleaseSequence = result.Release.Sequence,
            };
            submissionMayExist = true;
            ReleaseSelectedArtifactIfPresent(
                "The exact contribution is withdrawn. No CCB package bytes remain in local review memory.");
            ContributionStatusText.Text =
                $"The exact item is {result.State}. Local simulation deprecation release {result.Release.Sequence} was created. No remote publication occurred.";
        }
        catch (OperationCanceledException)
        {
            ContributionStatusText.Text = "Withdrawal canceled. Its result is unknown; query status before another action.";
        }
        catch (ContributionSubmissionException exception)
        {
            ContributionStatusText.Text = $"Withdrawal blocked: {exception.Code}. Query status before another action.";
        }
        catch (ArgumentException)
        {
            ContributionStatusText.Text = "Withdrawal blocked: simulation_origin_invalid. Nothing was sent.";
        }
        catch
        {
            ContributionStatusText.Text = "Withdrawal failed safely. Its result is unknown; query status before another action. The token was not saved or logged.";
        }
        finally
        {
            CompleteRequest();
        }
    }

    private void OnCancelSubmissionClick(object sender, RoutedEventArgs e) => requestCancellation?.Cancel();

    private bool TryStartRequest(out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(SimulationEndpointBox.Text.Trim(), UriKind.Absolute, out var parsedEndpoint) ||
            parsedEndpoint is null)
        {
            ContributorTokenBox.Clear();
            ClearNetworkApprovals();
            ContributionStatusText.Text = "Request blocked: simulation_origin_invalid. Nothing was sent.";
            return false;
        }

        endpoint = parsedEndpoint;

        if (simulationOriginBinding is not null && !SameOrigin(simulationOriginBinding, endpoint))
        {
            ContributorTokenBox.Clear();
            ClearNetworkApprovals();
            ContributionStatusText.Text = "Request blocked. Restore the original loopback simulation origin and reconcile this exact item first.";
            return false;
        }

        requestCancellation?.Dispose();
        requestCancellation = new CancellationTokenSource();
        requestInProgress = true;
        UpdateGate();
        return true;
    }

    private void CompleteRequest()
    {
        ContributorTokenBox.Clear();
        ClearNetworkApprovals();
        requestInProgress = false;
        UpdateGate();
    }

    private void ClearNetworkApprovals()
    {
        if (SubmissionApprovalBox is not null) SubmissionApprovalBox.IsChecked = false;
        if (StatusApprovalBox is not null) StatusApprovalBox.IsChecked = false;
        if (WithdrawalApprovalBox is not null) WithdrawalApprovalBox.IsChecked = false;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port && left.AbsolutePath == right.AbsolutePath &&
        left.Query == right.Query && left.Fragment == right.Fragment && left.UserInfo == right.UserInfo;

    private ContributionCandidate BuildCandidate()
    {
        var kind = IsCcbSelected() ? KnowledgeResourceKind.Ccb : KnowledgeResourceKind.Ckb;
        var license = LicenseBox.Text.Trim();
        return new ContributionCandidate(
            candidateId,
            ResourceIdBox.Text.Trim(),
            VersionBox.Text.Trim(),
            kind,
            TitleBox.Text.Trim(),
            BodyBox.Text,
            "urn:dcx:local-contribution-preview",
            license,
            [],
            [],
            ContributorPseudonymBox.Text.Trim(),
            kind == KnowledgeResourceKind.Ccb ? selectedArtifact?.Sha256 : null);
    }

    private void ReleaseSelectedArtifactIfPresent(string status)
    {
        if (selectedArtifact is not null) ReleaseSelectedArtifact(status);
    }

    private void UpdateGate()
    {
        if (PrepareButton is null || PreparationEnabledBox is null || ResourceIdBox is null ||
            VersionBox is null || ContributionKindBox is null || TitleBox is null || BodyBox is null ||
            LicenseBox is null || ContributorPseudonymBox is null || ArtifactGroup is null ||
            ArtifactPathBox is null || BrowseArtifactButton is null || LoadArtifactButton is null ||
            ClearArtifactButton is null ||
            ReviewButton is null || RightsBox is null || LicenseConfirmedBox is null ||
            SensitiveReviewedBox is null || ItemApprovalBox is null || SimulationEndpointBox is null ||
            ContributorTokenBox is null)
        {
            return;
        }

        var lifecycleOpen = submissionMayExist &&
                            simulationStatus?.State is not ContributionState.Withdrawn and
                                not ContributionState.Rejected;
        var candidateInputsEnabled = !requestInProgress && !artifactLoadInProgress && !lifecycleOpen;
        var networkInputsEnabled = !requestInProgress && !artifactLoadInProgress;
        var artifactReady = !IsCcbSelected() || selectedArtifact is not null;
        PreparationEnabledBox.IsEnabled = candidateInputsEnabled;
        ContributionKindBox.IsEnabled = candidateInputsEnabled;
        ResourceIdBox.IsEnabled = candidateInputsEnabled;
        VersionBox.IsEnabled = candidateInputsEnabled;
        TitleBox.IsEnabled = candidateInputsEnabled;
        BodyBox.IsEnabled = candidateInputsEnabled;
        LicenseBox.IsEnabled = candidateInputsEnabled;
        ContributorPseudonymBox.IsEnabled = candidateInputsEnabled;
        ArtifactGroup.IsEnabled = IsCcbSelected() && candidateInputsEnabled;
        ArtifactPathBox.IsEnabled = candidateInputsEnabled;
        BrowseArtifactButton.IsEnabled = candidateInputsEnabled;
        LoadArtifactButton.IsEnabled = candidateInputsEnabled;
        ClearArtifactButton.IsEnabled = candidateInputsEnabled && selectedArtifact is not null;
        ReviewButton.IsEnabled = candidateInputsEnabled && artifactReady;
        RightsBox.IsEnabled = candidateInputsEnabled;
        LicenseConfirmedBox.IsEnabled = candidateInputsEnabled;
        SensitiveReviewedBox.IsEnabled = candidateInputsEnabled;
        ItemApprovalBox.IsEnabled = candidateInputsEnabled && artifactReady;
        SimulationEndpointBox.IsEnabled = networkInputsEnabled;
        ContributorTokenBox.IsEnabled = networkInputsEnabled;
        PrepareButton.IsEnabled = PreparationEnabledBox.IsChecked == true &&
                                  candidateInputsEnabled && artifactReady &&
                                  RightsBox.IsChecked == true &&
                                  LicenseConfirmedBox.IsChecked == true &&
                                  SensitiveReviewedBox.IsChecked == true &&
                                  ItemApprovalBox.IsChecked == true &&
                                  approvedDigest is not null &&
                                  string.Equals(approvedDigest, reviewedDigest, StringComparison.Ordinal);
        if (SubmissionApprovalBox is null || StatusApprovalBox is null ||
            QuerySimulationStatusButton is null || WithdrawalApprovalBox is null ||
            WithdrawSimulationButton is null || CancelSubmissionButton is null) return;
        var hasEndpointAndToken = !string.IsNullOrWhiteSpace(SimulationEndpointBox.Text) &&
                                  !string.IsNullOrWhiteSpace(ContributorTokenBox.Password);
        var hasSubmissionArtifact = preparedContribution?.Candidate.Kind != KnowledgeResourceKind.Ccb ||
                                    selectedArtifact is not null;
        SubmissionApprovalBox.IsEnabled = preparedContribution is not null &&
                                          hasSubmissionArtifact && !requestInProgress &&
                                          !artifactLoadInProgress && !submissionMayExist;
        SubmitSimulationButton.IsEnabled = preparedContribution is not null &&
                                           hasSubmissionArtifact &&
                                           SubmissionApprovalBox.IsChecked == true &&
                                           !requestInProgress && !artifactLoadInProgress &&
                                           !submissionMayExist &&
                                           hasEndpointAndToken;
        StatusApprovalBox.IsEnabled = preparedContribution is not null && !requestInProgress &&
                                      !artifactLoadInProgress;
        QuerySimulationStatusButton.IsEnabled = preparedContribution is not null &&
                                                StatusApprovalBox.IsChecked == true &&
                                                !requestInProgress && !artifactLoadInProgress &&
                                                hasEndpointAndToken;
        WithdrawalApprovalBox.IsEnabled = preparedContribution is not null &&
                                          simulationStatus?.State == ContributionState.Approved &&
                                          !requestInProgress && !artifactLoadInProgress;
        WithdrawSimulationButton.IsEnabled = preparedContribution is not null &&
                                             simulationStatus?.State == ContributionState.Approved &&
                                             WithdrawalApprovalBox.IsChecked == true &&
                                             !requestInProgress && !artifactLoadInProgress &&
                                             hasEndpointAndToken;
        CancelSubmissionButton.IsEnabled = requestInProgress;
    }
}
