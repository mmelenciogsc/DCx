using System.Windows;
using System.Windows.Controls;
using DCx.Core.Contributions;
using DCx.Core.Knowledge;

namespace DCx.Desktop;

public partial class ContributionsWindow : Window
{
    private readonly ContributionScanner scanner = new();
    private readonly ContributionPreparationService preparationService;
    private readonly string candidateId = $"candidate:{Guid.NewGuid():N}";
    private string? reviewedDigest;
    private string? approvedDigest;
    private string? approvalNonce;
    private DateTimeOffset approvalTime;
    private PreparedContribution? preparedContribution;
    private CancellationTokenSource? submissionCancellation;
    private bool submissionInProgress;

    public ContributionsWindow()
    {
        preparationService = new ContributionPreparationService(scanner);
        InitializeComponent();
        Closed += (_, _) =>
        {
            submissionCancellation?.Cancel();
            submissionCancellation?.Dispose();
            submissionCancellation = null;
        };
    }

    private void OnCandidateChanged(object sender, RoutedEventArgs e)
    {
        reviewedDigest = null;
        approvedDigest = null;
        approvalNonce = null;
        preparedContribution = null;
        if (ItemApprovalBox is not null)
        {
            ItemApprovalBox.IsChecked = false;
        }

        if (FindingsList is not null)
        {
            FindingsList.ItemsSource = null;
        }

        if (SubmissionApprovalBox is not null)
        {
            SubmissionApprovalBox.IsChecked = false;
        }

        UpdateGate();
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();
            var scan = scanner.Scan(candidate);
            reviewedDigest = ContributionCodec.CandidateSha256(candidate);
            FindingsList.ItemsSource = scan.Findings.Count == 0
                ? ["No automated findings. Human review is still required."]
                : scan.Findings.Select(item => $"{item.Severity}: {item.Code}").ToArray();
            ContributionStatusText.Text = scan.IsEligible
                ? "Automated scan found no blocking issue. Review and approve this exact item if appropriate."
                : "Findings require quarantine review. Values are not echoed into this list.";
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
            var digest = ContributionCodec.CandidateSha256(BuildCandidate());
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
            preparedContribution = preparationService.Prepare(
                new ContributionPreferences(PreparationEnabledBox.IsChecked == true),
                candidate,
                new ContributionAttestation(
                    RightsBox.IsChecked == true,
                    LicenseConfirmedBox.IsChecked == true,
                    SensitiveReviewedBox.IsChecked == true),
                new ContributionItemApproval(candidateId, approvedDigest!, approvalNonce!, approvalTime),
                DateTimeOffset.UtcNow);
            ContributionStatusText.Text =
                $"Prepared locally as {preparedContribution.State}. Findings: {preparedContribution.Scan.Findings.Count}. No upload or publication occurred. Optional loopback submission now requires a separate approval.";
            SubmissionApprovalBox.IsChecked = false;
            ItemApprovalBox.IsChecked = false;
            approvedDigest = null;
            approvalNonce = null;
            PrepareButton.IsEnabled = false;
        }
        catch (ContributionPolicyException exception)
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
        if (SubmissionApprovalBox is not null)
        {
            SubmissionApprovalBox.IsChecked = false;
        }

        UpdateGate();
    }

    private void OnSubmissionApprovalChanged(object sender, RoutedEventArgs e) => UpdateGate();

    private async void OnSubmitSimulationClick(object sender, RoutedEventArgs e)
    {
        if (preparedContribution is null || SubmissionApprovalBox.IsChecked != true || submissionInProgress)
        {
            ContributionStatusText.Text = "Submission blocked. Prepare the exact item and approve this separate send first.";
            return;
        }

        if (!Uri.TryCreate(SimulationEndpointBox.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            SubmissionApprovalBox.IsChecked = false;
            ContributionStatusText.Text = "Submission blocked: simulation_origin_invalid. Nothing was sent.";
            return;
        }

        submissionCancellation?.Dispose();
        submissionCancellation = new CancellationTokenSource();
        submissionInProgress = true;
        UpdateGate();
        ContributionStatusText.Text = "Sending the exact prepared item to the loopback simulation for quarantine. No publication is authorized.";
        try
        {
            using var client = new ContributionSubmissionClient(endpoint, TimeSpan.FromSeconds(30));
            var result = await client.SubmitAsync(
                preparedContribution,
                ContributorTokenBox.Password,
                cancellationToken: submissionCancellation.Token);
            ContributionStatusText.Text =
                $"The local simulation accepted {result.SubmissionId} into {result.State}. No approval or publication occurred. The token was not saved.";
        }
        catch (OperationCanceledException)
        {
            ContributionStatusText.Text = "Local simulation submission canceled. Acceptance is unknown; check the server before retrying.";
        }
        catch (ContributionSubmissionException exception)
        {
            ContributionStatusText.Text = $"Local simulation submission blocked: {exception.Code}. The token was not saved.";
        }
        catch
        {
            ContributionStatusText.Text = "Local simulation submission failed safely. The token was not saved or logged.";
        }
        finally
        {
            ContributorTokenBox.Clear();
            SubmissionApprovalBox.IsChecked = false;
            submissionInProgress = false;
            UpdateGate();
        }
    }

    private void OnCancelSubmissionClick(object sender, RoutedEventArgs e) => submissionCancellation?.Cancel();

    private ContributionCandidate BuildCandidate()
    {
        var license = (LicenseBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        return new ContributionCandidate(
            candidateId,
            ResourceIdBox.Text.Trim(),
            "1.0.0",
            KnowledgeResourceKind.Ckb,
            TitleBox.Text.Trim(),
            BodyBox.Text,
            "urn:dcx:local-contribution-preview",
            license,
            [],
            [],
            ContributorPseudonymBox.Text.Trim());
    }

    private void UpdateGate()
    {
        if (PrepareButton is null)
        {
            return;
        }

        PrepareButton.IsEnabled = PreparationEnabledBox.IsChecked == true &&
                                  RightsBox.IsChecked == true &&
                                  LicenseConfirmedBox.IsChecked == true &&
                                  SensitiveReviewedBox.IsChecked == true &&
                                  ItemApprovalBox.IsChecked == true &&
                                  approvedDigest is not null &&
                                  string.Equals(approvedDigest, reviewedDigest, StringComparison.Ordinal);
        if (SubmissionApprovalBox is null) return;
        SubmissionApprovalBox.IsEnabled = preparedContribution is not null && !submissionInProgress;
        SubmitSimulationButton.IsEnabled = preparedContribution is not null &&
                                           SubmissionApprovalBox.IsChecked == true &&
                                           !submissionInProgress &&
                                           !string.IsNullOrWhiteSpace(SimulationEndpointBox.Text) &&
                                           !string.IsNullOrWhiteSpace(ContributorTokenBox.Password);
        CancelSubmissionButton.IsEnabled = submissionInProgress;
    }
}
