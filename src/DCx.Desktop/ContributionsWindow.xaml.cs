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

    public ContributionsWindow()
    {
        preparationService = new ContributionPreparationService(scanner);
        InitializeComponent();
    }

    private void OnCandidateChanged(object sender, RoutedEventArgs e)
    {
        reviewedDigest = null;
        approvedDigest = null;
        approvalNonce = null;
        if (ItemApprovalBox is not null)
        {
            ItemApprovalBox.IsChecked = false;
        }

        if (FindingsList is not null)
        {
            FindingsList.ItemsSource = null;
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
        catch (Exception exception)
        {
            ContributionStatusText.Text = $"The item cannot be reviewed: {exception.Message}";
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
        catch (Exception exception)
        {
            ItemApprovalBox.IsChecked = false;
            ContributionStatusText.Text = $"The item cannot be approved: {exception.Message}";
        }

        UpdateGate();
    }

    private void OnGateChanged(object sender, RoutedEventArgs e) => UpdateGate();

    private void OnPrepareClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();
            var prepared = preparationService.Prepare(
                new ContributionPreferences(PreparationEnabledBox.IsChecked == true),
                candidate,
                new ContributionAttestation(
                    RightsBox.IsChecked == true,
                    LicenseConfirmedBox.IsChecked == true,
                    SensitiveReviewedBox.IsChecked == true),
                new ContributionItemApproval(candidateId, approvedDigest!, approvalNonce!, approvalTime),
                DateTimeOffset.UtcNow);
            ContributionStatusText.Text =
                $"Prepared locally as {prepared.State}. Findings: {prepared.Scan.Findings.Count}. No upload or publication occurred.";
            PrepareButton.IsEnabled = false;
        }
        catch (ContributionPolicyException exception)
        {
            ContributionStatusText.Text = $"Preparation blocked: {exception.Code}. Nothing was uploaded.";
        }
        catch (Exception exception)
        {
            ContributionStatusText.Text = $"Preparation failed: {exception.Message}. Nothing was uploaded.";
        }
    }

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
            "local:private-pseudonym");
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
    }
}
