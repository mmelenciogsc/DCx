using System.Security.Cryptography;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;

namespace DCx.Core.Tests.Contributions;

public sealed class ContributionGovernanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-p10-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task EligibleSubmission_RequiresReviewPropagatesAndWithdraws()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(Path.Combine(root, "registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(Path.Combine(root, "governance"), registry);
        var candidate = Candidate("candidate:approved", "resource:approved", "A thorough keyboard accessibility pattern with deterministic validation guidance.");
        var prepared = Prepare(candidate, "nonce-approved-1234567890");

        Assert.Equal(ContributionState.Quarantined, await governance.IntakeAsync(prepared, now));
        Assert.Null(await registry.GetLatestAsync());
        var release = await governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now);

        var client = await GlobalReplicaClient.OpenAsync(Path.Combine(root, "client"), registry.TrustRootSubjectPublicKeyInfo);
        await client.SynchronizeAsync(new RepositoryRegistryTransport(registry));
        Assert.Equal(release.Sha256, (await client.GetStatusAsync()).ReleaseSha256);
        Assert.Equal("resource:approved", Assert.Single(await client.SearchAsync("keyboard accessibility", null)).ResourceId);

        await governance.WithdrawAsync(prepared.SubmissionId, now.AddDays(1));
        await client.SynchronizeAsync(new RepositoryRegistryTransport(registry));
        Assert.True(Assert.Single(await client.SearchAsync("keyboard accessibility", null)).Deprecated);
    }

    [Fact]
    public async Task RiskyAndDummySubmissions_RemainQuarantinedAndCannotPropagate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(Path.Combine(root, "registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(Path.Combine(root, "governance"), registry);
        var candidate = Candidate(
            "candidate:risky",
            "resource:risky",
            "hello world sk-abcdefghijklmnopqrstuvwxyz123456 someone@example.test");
        var prepared = Prepare(candidate, "nonce-risky-123456789012");

        Assert.Equal(ContributionState.Quarantined, await governance.IntakeAsync(prepared, now));
        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));
        Assert.Equal("quarantine_findings_present", exception.Code);
        Assert.Null(await registry.GetLatestAsync());
    }

    [Fact]
    public async Task UsageReceipts_QualifyOnceAndExcludeReplaySelfUseAndBadIntegrity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(Path.Combine(root, "registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(Path.Combine(root, "governance"), registry);
        var candidate = Candidate("candidate:usage", "resource:usage", "A reusable and independently verified deterministic accessibility capability.");
        var prepared = Prepare(candidate, "nonce-usage-123456789012");
        await governance.IntakeAsync(prepared, now);
        await governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now);
        var integrityKey = Enumerable.Repeat((byte)17, 32).ToArray();
        var receipt = new QualifiedUsageReceipt(
            "receipt:one", "installation:one", "entitlement:one", candidate.ResourceId, candidate.Version,
            "usage-nonce-1234567890", now, true, "verified-success");
        var signature = UsageReceiptPolicy.Sign(receipt, integrityKey);

        var qualified = await governance.EvaluateUsageAsync(
            receipt,
            signature,
            new AuthoritativeUsageContext(true, "consumer:other", integrityKey, now));
        var replay = await governance.EvaluateUsageAsync(
            receipt,
            signature,
            new AuthoritativeUsageContext(true, "consumer:other", integrityKey, now));
        var selfReceipt = receipt with { ReceiptId = "receipt:self", Nonce = "usage-nonce-self-123456" };
        var selfUse = await governance.EvaluateUsageAsync(
            selfReceipt,
            UsageReceiptPolicy.Sign(selfReceipt, integrityKey),
            new AuthoritativeUsageContext(true, candidate.ContributorPseudonym, integrityKey, now));
        var badReceipt = receipt with { ReceiptId = "receipt:bad", Nonce = "usage-nonce-bad-1234567" };
        var badIntegrity = await governance.EvaluateUsageAsync(
            badReceipt,
            "not-base64",
            new AuthoritativeUsageContext(true, "consumer:other", integrityKey, now));

        Assert.True(qualified.Qualified);
        Assert.Equal("replay", replay.Code);
        Assert.Equal("self_use", selfUse.Code);
        Assert.Equal("integrity_invalid", badIntegrity.Code);
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    private ContributionCandidate Candidate(string candidateId, string resourceId, string body) => new(
        candidateId,
        resourceId,
        "1",
        KnowledgeResourceKind.Ckb,
        "Reviewed contribution fixture",
        body,
        "https://example.test/contribution",
        "CC-BY-4.0",
        [],
        [],
        "contributor:one");

    private PreparedContribution Prepare(ContributionCandidate candidate, string nonce)
    {
        var approval = new ContributionItemApproval(candidate.CandidateId, ContributionCodec.CandidateSha256(candidate), nonce, now);
        return new ContributionPreparationService(new ContributionScanner()).Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            approval,
            now);
    }
}
