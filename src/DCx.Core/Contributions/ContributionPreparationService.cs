using System.Security.Cryptography;

namespace DCx.Core.Contributions;

public sealed class ContributionPreparationService(ContributionScanner scanner)
{
    public PreparedContribution Prepare(
        ContributionPreferences preferences,
        ContributionCandidate candidate,
        ContributionAttestation attestation,
        ContributionItemApproval approval,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(approval);
        if (!preferences.PreparationEnabled)
        {
            throw new ContributionPolicyException("contribution_opt_in_required");
        }

        if (!attestation.OwnsOrMayShare || !attestation.LicenseConfirmed || !attestation.SensitiveDataReviewed)
        {
            throw new ContributionPolicyException("contribution_attestation_required");
        }

        if (!string.Equals(candidate.CandidateId, approval.CandidateId, StringComparison.Ordinal) ||
            !IsSha256(approval.CandidateSha256) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ContributionCodec.CandidateSha256(candidate)),
                Convert.FromHexString(approval.CandidateSha256)) ||
            string.IsNullOrWhiteSpace(approval.ApprovalNonce) || approval.ApprovalNonce.Length is < 16 or > 128 ||
            approval.ApprovedUtc > now || now - approval.ApprovedUtc > TimeSpan.FromMinutes(30))
        {
            throw new ContributionPolicyException("item_approval_invalid_or_expired");
        }

        var scan = scanner.Scan(candidate);
        return new PreparedContribution(
            $"submission:{Guid.NewGuid():N}",
            candidate,
            attestation,
            approval,
            scan,
            ContributionState.Quarantined,
            now);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
