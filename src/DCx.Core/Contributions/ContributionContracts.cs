using DCx.Core.Knowledge;

namespace DCx.Core.Contributions;

public sealed record ContributionPreferences(bool PreparationEnabled = false);

public sealed record ContributionCreditEdge(string ParentResourceId, decimal Weight);

public sealed record ContributionCandidate(
    string CandidateId,
    string ResourceId,
    string Version,
    KnowledgeResourceKind Kind,
    string Title,
    string Body,
    string SourceUri,
    string License,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ContributionCreditEdge> DerivedFrom,
    string ContributorPseudonym,
    string? ArtifactSha256 = null);

public sealed record ContributionAttestation(
    bool OwnsOrMayShare,
    bool LicenseConfirmed,
    bool SensitiveDataReviewed);

public sealed record ContributionItemApproval(
    string CandidateId,
    string CandidateSha256,
    string ApprovalNonce,
    DateTimeOffset ApprovedUtc);

public enum ContributionFindingSeverity
{
    Information = 0,
    Quarantine = 1,
    Reject = 2,
}

public sealed record ContributionFinding(string Code, ContributionFindingSeverity Severity);

public sealed record ContributionScanResult(IReadOnlyList<ContributionFinding> Findings)
{
    public bool IsEligible => Findings.All(item => item.Severity == ContributionFindingSeverity.Information);
}

public enum ContributionState
{
    Quarantined = 0,
    Approved = 1,
    Rejected = 2,
    Withdrawn = 3,
}

public sealed record PreparedContribution(
    string SubmissionId,
    ContributionCandidate Candidate,
    ContributionAttestation Attestation,
    ContributionItemApproval Approval,
    ContributionScanResult Scan,
    ContributionState State,
    DateTimeOffset PreparedUtc);

public sealed class ContributionPolicyException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
