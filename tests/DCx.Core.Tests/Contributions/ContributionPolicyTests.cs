using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DCx.Core.Contributions;
using DCx.Core.Knowledge;

namespace DCx.Core.Tests.Contributions;

public sealed class ContributionPolicyTests
{
    private readonly ContributionPreparationService service = new(new ContributionScanner());
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public void Preparation_DefaultOptInIsFalseAndBlocksPreparation()
    {
        var preferences = new ContributionPreferences();
        var candidate = Candidate("candidate:one", "A detailed accessibility guide with enough original material for review.");
        var approval = Approval(candidate);

        var exception = Assert.Throws<ContributionPolicyException>(() => service.Prepare(
            preferences,
            candidate,
            new ContributionAttestation(true, true, true),
            approval,
            now));

        Assert.False(preferences.PreparationEnabled);
        Assert.Equal("contribution_opt_in_required", exception.Code);
    }

    [Fact]
    public void Preparation_RequiresDigestBoundItemApprovalAndAlwaysStartsQuarantined()
    {
        var candidate = Candidate("candidate:one", "A detailed accessibility guide with enough original material for review.");
        var prepared = service.Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            Approval(candidate),
            now);

        Assert.Equal(ContributionState.Quarantined, prepared.State);
        Assert.True(prepared.Scan.IsEligible);
        var changed = candidate with { Body = candidate.Body + " Changed after approval." };
        Assert.Equal("item_approval_invalid_or_expired", Assert.Throws<ContributionPolicyException>(() => service.Prepare(
            new ContributionPreferences(true),
            changed,
            new ContributionAttestation(true, true, true),
            Approval(candidate),
            now)).Code);
    }

    [Fact]
    public void Scanner_QuarantinesDummyPiiAndRejectsSecretsWithoutEchoingValues()
    {
        var secret = "sk-abcdefghijklmnopqrstuvwxyz123456";
        var candidate = Candidate(
            "candidate:risky",
            $"hello world {secret} contact someone@example.test or 212-555-0199");

        var scan = new ContributionScanner().Scan(candidate);

        Assert.False(scan.IsEligible);
        Assert.Contains(scan.Findings, item => item.Code == "secret.api_credential" && item.Severity == ContributionFindingSeverity.Reject);
        Assert.Contains(scan.Findings, item => item.Code == "pii.email");
        Assert.DoesNotContain(scan.Findings, item => item.Code.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Lineage_RejectsCyclesAndOverAllocation()
    {
        var cycle = new Dictionary<string, IReadOnlyList<ContributionCreditEdge>>
        {
            ["a"] = [new ContributionCreditEdge("b", 0.5m)],
            ["b"] = [new ContributionCreditEdge("a", 0.5m)],
        };
        var over = new Dictionary<string, IReadOnlyList<ContributionCreditEdge>>
        {
            ["c"] = [new ContributionCreditEdge("a", 0.6m), new ContributionCreditEdge("b", 0.5m)],
        };

        Assert.Equal("lineage_cycle", Assert.Throws<ContributionPolicyException>(() => ContributionLineageGraph.Validate(cycle)).Code);
        Assert.Equal("lineage_invalid", Assert.Throws<ContributionPolicyException>(() => ContributionLineageGraph.Validate(over)).Code);
    }

    [Fact]
    public void Receipt_IsMinimalSignedAndFraudFiltered()
    {
        var receipt = new QualifiedUsageReceipt(
            "receipt:one", "installation:one", "entitlement:one", "resource:one", "1", "nonce:1234567890123456",
            now, true, "verified-success");
        var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var signature = UsageReceiptPolicy.Sign(receipt, key);
        var eligible = new UsageReceiptContext(
            true,
            true,
            false,
            receipt.InstallationPseudonym,
            receipt.EntitlementId,
            0,
            false,
            false,
            now);

        Assert.True(UsageReceiptPolicy.Verify(receipt, signature, key));
        Assert.True(UsageReceiptPolicy.Evaluate(receipt, eligible).Qualified);
        Assert.Equal("replay", UsageReceiptPolicy.Evaluate(receipt, eligible with { ReceiptIdSeen = true }).Code);
        Assert.Equal("self_use", UsageReceiptPolicy.Evaluate(receipt, eligible with { IsContributorSelfUse = true }).Code);
        Assert.Equal("identity_mismatch", UsageReceiptPolicy.Evaluate(
            receipt,
            eligible with { AuthoritativeInstallationPseudonym = "installation:other" }).Code);
        Assert.Equal("identity_mismatch", UsageReceiptPolicy.Evaluate(
            receipt,
            eligible with { AuthoritativeEntitlementId = "entitlement:other" }).Code);
        Assert.Equal("rate_anomaly", UsageReceiptPolicy.Evaluate(
            receipt,
            eligible with { PriorQualifiedEventsForDay = UsageReceiptPolicy.MaximumQualifiedEventsPerResourceDay }).Code);
        Assert.False(UsageReceiptPolicy.Verify(receipt with { ResourceId = "resource:other" }, signature, key));
        Assert.False(UsageReceiptPolicy.Verify(receipt, new string('a', 129), key));
        Assert.False(UsageReceiptPolicy.Verify(receipt, signature, key.AsSpan(0, 16)));
        Assert.Throws<ArgumentException>(() => UsageReceiptPolicy.Sign(
            receipt with { ReceiptId = new string('x', 129) }, key));

        var json = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportEnvelope_RoundTripsStrictApprovedMetadataAndArtifactBytes()
    {
        var candidate = Candidate(
            "candidate:transport",
            "A bounded contribution transport fixture with exact reviewed metadata.");
        var prepared = service.Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            Approval(candidate),
            now);
        byte[] artifact = [0x50, 0x4b, 0x03, 0x04, 0x2a];

        var encoded = ContributionTransportCodec.Encode(prepared, artifact);
        var decoded = ContributionTransportCodec.Decode(encoded);

        Assert.Equal(ContributionTransportCodec.MediaType, "application/vnd.dcx.contribution.v1+binary");
        Assert.Equal(prepared.SubmissionId, decoded.Submission.SubmissionId);
        Assert.Equal(
            ContributionCodec.CandidateSha256(prepared.Candidate),
            ContributionCodec.CandidateSha256(decoded.Submission.Candidate));
        Assert.Equal(prepared.Attestation, decoded.Submission.Attestation);
        Assert.Equal(prepared.Approval, decoded.Submission.Approval);
        Assert.Equal(prepared.State, decoded.Submission.State);
        Assert.Equal(prepared.PreparedUtc, decoded.Submission.PreparedUtc);
        Assert.Equal(
            prepared.Scan.Findings.Select(finding => finding.Code),
            decoded.Submission.Scan.Findings.Select(finding => finding.Code));
        Assert.Equal(artifact, decoded.Artifact);
    }

    [Fact]
    public void TransportEnvelope_RejectsVersionTruncationAndOversizedMetadata()
    {
        var candidate = Candidate(
            "candidate:transport-denial",
            "A bounded contribution transport denial fixture.");
        var prepared = service.Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            Approval(candidate),
            now);
        var encoded = ContributionTransportCodec.Encode(prepared);
        var changedVersion = encoded.ToArray();
        changedVersion[4] = 2;
        var metadataLength = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(5, 4));
        var metadata = Encoding.UTF8.GetString(
            encoded,
            ContributionTransportCodec.HeaderBytes,
            metadataLength);
        var duplicateMetadata = Encoding.UTF8.GetBytes(
            metadata.Insert(1, $"\"submissionId\":\"{prepared.SubmissionId}\","));
        var duplicateEnvelope = new byte[ContributionTransportCodec.HeaderBytes + duplicateMetadata.Length];
        encoded.AsSpan(0, ContributionTransportCodec.HeaderBytes).CopyTo(duplicateEnvelope);
        BinaryPrimitives.WriteInt32BigEndian(duplicateEnvelope.AsSpan(5, 4), duplicateMetadata.Length);
        duplicateMetadata.CopyTo(duplicateEnvelope.AsSpan(ContributionTransportCodec.HeaderBytes));

        Assert.Equal("contribution_envelope_invalid", Assert.Throws<ContributionPolicyException>(() =>
            ContributionTransportCodec.Decode(changedVersion)).Code);
        Assert.Equal("contribution_envelope_invalid", Assert.Throws<ContributionPolicyException>(() =>
            ContributionTransportCodec.Decode(encoded.AsSpan(0, ContributionTransportCodec.HeaderBytes))).Code);
        Assert.Equal("contribution_envelope_invalid", Assert.Throws<ContributionPolicyException>(() =>
            ContributionTransportCodec.Decode(duplicateEnvelope)).Code);
        Assert.Equal("contribution_envelope_size_invalid", Assert.Throws<ContributionPolicyException>(() =>
            ContributionTransportCodec.Encode(prepared with
            {
                Candidate = candidate with { Body = new string('x', ContributionTransportCodec.MaximumMetadataBytes) },
            })).Code);
    }

    private ContributionCandidate Candidate(string id, string body) => new(
        id,
        $"resource:{id.Split(':')[1]}",
        "1",
        KnowledgeResourceKind.Ckb,
        "Accessible contribution fixture",
        body,
        "https://example.test/source",
        "CC-BY-4.0",
        [],
        [],
        "contributor:one");

    private ContributionItemApproval Approval(ContributionCandidate candidate) => new(
        candidate.CandidateId,
        ContributionCodec.CandidateSha256(candidate),
        "approval-nonce-1234567890",
        now);
}
