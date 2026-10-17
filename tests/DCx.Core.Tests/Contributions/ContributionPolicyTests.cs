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
        var eligible = new UsageReceiptContext(true, true, false, 0, false, false, now);

        Assert.True(UsageReceiptPolicy.Verify(receipt, signature, key));
        Assert.True(UsageReceiptPolicy.Evaluate(receipt, eligible).Qualified);
        Assert.Equal("replay", UsageReceiptPolicy.Evaluate(receipt, eligible with { ReceiptIdSeen = true }).Code);
        Assert.Equal("self_use", UsageReceiptPolicy.Evaluate(receipt, eligible with { IsContributorSelfUse = true }).Code);
        Assert.Equal("rate_anomaly", UsageReceiptPolicy.Evaluate(
            receipt,
            eligible with { PriorQualifiedEventsForDay = UsageReceiptPolicy.MaximumQualifiedEventsPerResourceDay }).Code);
        Assert.False(UsageReceiptPolicy.Verify(receipt with { ResourceId = "resource:other" }, signature, key));

        var json = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
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
