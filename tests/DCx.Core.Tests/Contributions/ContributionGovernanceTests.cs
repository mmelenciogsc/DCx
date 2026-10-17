using System.Security.Cryptography;
using System.Text;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

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

    [Fact]
    public async Task ApprovalAndWithdrawal_ReconcileCommittedRegistryOperationsWithoutDuplicateReleases()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "reconciliation-registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(
            Path.Combine(root, "reconciliation-governance"), registry);
        var candidate = Candidate(
            "candidate:reconciliation",
            "resource:reconciliation",
            "A deterministic fixture for cross-database publication recovery and retry safety.");
        var prepared = Prepare(candidate, "nonce-reconciliation-1234");
        await governance.IntakeAsync(prepared, now);
        var resource = new GlobalResourceRecord(
            candidate.ResourceId,
            candidate.Version,
            candidate.Kind,
            candidate.Title,
            candidate.Body,
            candidate.SourceUri,
            candidate.License,
            candidate.Capabilities);

        var committedApproval = await registry.PublishIdempotentAsync(
            PublicationOperationId("approval", prepared.SubmissionId),
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
            now);
        var recoveredApproval = await governance.ApproveAndPublishAsync(
            prepared.SubmissionId, "reviewer:fixture", now.AddMinutes(1));

        var committedWithdrawal = await registry.PublishIdempotentAsync(
            PublicationOperationId("withdrawal", prepared.SubmissionId),
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Deprecate, candidate.ResourceId, candidate.Version)],
            now.AddDays(1));
        var recoveredWithdrawal = await governance.WithdrawAsync(prepared.SubmissionId, now.AddDays(1).AddMinutes(1));

        Assert.Equal(committedApproval, recoveredApproval);
        Assert.Equal(committedWithdrawal, recoveredWithdrawal);
        Assert.Equal(2, (await registry.GetLatestAsync())!.Sequence);
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "reconciliation-client"), registry.TrustRootSubjectPublicKeyInfo);
        await client.SynchronizeAsync(new RepositoryRegistryTransport(registry));
        Assert.True(Assert.Single(await client.SearchAsync("cross database recovery", null)).Deprecated);
    }

    [Fact]
    public async Task CcbSubmission_RequiresAuthoritativeSandboxAndPropagatesTestedArtifact()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "ccb-registry"), key.ExportPkcs8PrivateKey());
        var executor = new FixtureSandboxExecutor();
        var governance = await ContributionGovernanceService.OpenAsync(
            Path.Combine(root, "ccb-governance"), registry, executor);
        var artifact = CcbContributionPackageTests.CreatePackage();
        var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact));
        var candidate = CcbCandidate("candidate:ccb", "tool:community-upper", artifactSha256);
        var prepared = Prepare(candidate, "nonce-ccb-approved-123456");

        Assert.Equal(ContributionState.Quarantined, await governance.IntakeAsync(prepared, artifact, now));
        var release = await governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now);

        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "ccb-client"), registry.TrustRootSubjectPublicKeyInfo);
        var transport = new RepositoryRegistryTransport(registry);
        await client.SynchronizeAsync(transport);
        var resource = Assert.Single(await client.SearchAsync("community upper", null));
        var artifactReference = Assert.IsType<GlobalArtifactReference>(resource.Artifact);
        var downloaded = await client.GetArtifactAsync(artifactReference.Sha256, transport);

        Assert.Equal(release.Sha256, (await client.GetStatusAsync()).ReleaseSha256);
        Assert.Equal(KnowledgeResourceKind.Ccb, resource.Kind);
        Assert.Equal(2, executor.ExecutionCount);
        Assert.Equal(artifactSha256, artifactReference.Sha256);
        Assert.Equal(artifact, await File.ReadAllBytesAsync(downloaded));

        await using var evidenceConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "ccb-governance", "contribution-governance.sqlite3"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await evidenceConnection.OpenAsync();
        var evidenceCommand = evidenceConnection.CreateCommand();
        evidenceCommand.CommandText = "SELECT COUNT(*) FROM ccb_sandbox_reviews WHERE submission_id = $submission AND passed = 1;";
        evidenceCommand.Parameters.AddWithValue("$submission", prepared.SubmissionId);
        Assert.Equal(2L, (long)(await evidenceCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CcbSubmission_WithoutSandboxOrWithChangedArtifactRemainsQuarantined()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "ccb-denied-registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(
            Path.Combine(root, "ccb-denied-governance"), registry);
        var artifact = CcbContributionPackageTests.CreatePackage();
        var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact));
        var candidate = CcbCandidate("candidate:ccb-denied", "tool:community-upper", artifactSha256);
        var prepared = Prepare(candidate, "nonce-ccb-denied-1234567");
        await governance.IntakeAsync(prepared, artifact, now);

        var noSandbox = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));
        var changed = artifact.ToArray();
        changed[^1] ^= 0x01;
        var changedCandidate = CcbCandidate("candidate:ccb-changed", "tool:community-upper", artifactSha256);
        var changedPrepared = Prepare(changedCandidate, "nonce-ccb-changed-123456");
        var digestFailure = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.IntakeAsync(changedPrepared, changed, now));

        Assert.Equal("ccb_sandbox_review_required", noSandbox.Code);
        Assert.Equal("ccb_artifact_digest_invalid", digestFailure.Code);
        Assert.Null(await registry.GetLatestAsync());
    }

    [Fact]
    public async Task CcbSubmission_WithSandboxOutputMismatchCannotPropagate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "ccb-mismatch-registry"), key.ExportPkcs8PrivateKey());
        var governance = await ContributionGovernanceService.OpenAsync(
            Path.Combine(root, "ccb-mismatch-governance"), registry, new FixtureSandboxExecutor(outputMismatch: true));
        var artifact = CcbContributionPackageTests.CreatePackage();
        var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact));
        var candidate = CcbCandidate("candidate:ccb-mismatch", "tool:community-upper", artifactSha256);
        var prepared = Prepare(candidate, "nonce-ccb-mismatch-12345");
        await governance.IntakeAsync(prepared, artifact, now);

        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        var suspiciousArtifact = CcbContributionPackageTests.CreatePackage(entrypointText: "rm -rf /\n");
        var suspiciousSha256 = Convert.ToHexStringLower(SHA256.HashData(suspiciousArtifact));
        var suspiciousCandidate = CcbCandidate(
            "candidate:ccb-suspicious", "tool:community-upper", suspiciousSha256);
        var suspiciousPrepared = Prepare(suspiciousCandidate, "nonce-ccb-suspicious-1234");
        await governance.IntakeAsync(suspiciousPrepared, suspiciousArtifact, now);
        var suspicious = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(suspiciousPrepared.SubmissionId, "reviewer:fixture", now));

        Assert.Equal("ccb_sandbox_output_mismatch", exception.Code);
        Assert.Equal("quarantine_findings_present", suspicious.Code);
        Assert.Null(await registry.GetLatestAsync());
    }

    [Fact]
    public async Task CcbSubmission_RejectsMetadataMismatchScannedTestInputAndStagedTamper()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "ccb-hardening-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "ccb-hardening-governance");
        var governance = await ContributionGovernanceService.OpenAsync(
            governanceRoot, registry, new FixtureSandboxExecutor());

        var mismatched = CcbContributionPackageTests.CreatePackage(
            licenseId: "Apache-2.0",
            licenseText: "Apache License 2.0\n");
        var mismatchedSha256 = Convert.ToHexStringLower(SHA256.HashData(mismatched));
        var mismatchedPrepared = Prepare(
            CcbCandidate("candidate:ccb-license", "tool:community-upper", mismatchedSha256),
            "nonce-ccb-license-123456");
        var metadataFailure = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.IntakeAsync(mismatchedPrepared, mismatched, now));

        var suspiciousInput = CcbContributionPackageTests.CreatePackage(testInputBase64: "cm0gLXJmIC8=");
        var suspiciousInputSha256 = Convert.ToHexStringLower(SHA256.HashData(suspiciousInput));
        var suspiciousInputPrepared = Prepare(
            CcbCandidate("candidate:ccb-input", "tool:community-upper", suspiciousInputSha256),
            "nonce-ccb-input-12345678");
        await governance.IntakeAsync(suspiciousInputPrepared, suspiciousInput, now);
        var inputFailure = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(suspiciousInputPrepared.SubmissionId, "reviewer:fixture", now));

        var clean = CcbContributionPackageTests.CreatePackage();
        var cleanSha256 = Convert.ToHexStringLower(SHA256.HashData(clean));
        var cleanPrepared = Prepare(
            CcbCandidate("candidate:ccb-tamper", "tool:community-upper", cleanSha256),
            "nonce-ccb-tamper-1234567");
        await governance.IntakeAsync(cleanPrepared, clean, now);
        var stagedPath = Path.Combine(
            governanceRoot,
            "quarantine",
            "artifacts",
            "sha256",
            cleanSha256[..2],
            $"{cleanSha256}.zip");
        var changed = clean.ToArray();
        changed[^1] ^= 0x01;
        await File.WriteAllBytesAsync(stagedPath, changed);
        var tamperFailure = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.ApproveAndPublishAsync(cleanPrepared.SubmissionId, "reviewer:fixture", now));

        Assert.Equal("ccb_artifact_metadata_mismatch", metadataFailure.Code);
        Assert.Equal("quarantine_findings_present", inputFailure.Code);
        Assert.Equal("ccb_artifact_tampered", tamperFailure.Code);
        Assert.Null(await registry.GetLatestAsync());
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

    private ContributionCandidate CcbCandidate(string candidateId, string resourceId, string artifactSha256) => new(
        candidateId,
        resourceId,
        "1.0.0",
        KnowledgeResourceKind.Ccb,
        "Community uppercase tool",
        "A sandboxed uppercase transformation with two deterministic verification fixtures.",
        "https://example.test/community-upper",
        "MIT",
        ["text.upper"],
        [],
        "contributor:one",
        artifactSha256);

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

    private static string PublicationOperationId(string action, string submissionId) =>
        $"contribution-{action}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(submissionId)))}";

    private sealed class FixtureSandboxExecutor(bool outputMismatch = false) : ICcbSandboxExecutor
    {
        private static readonly string LauncherSha256 = Digest("fixture-launcher");
        private static readonly string BackendSha256 = Digest("fixture-backend");
        private static readonly string RuntimeSha256 = Digest("fixture-runtime");

        public int ExecutionCount { get; private set; }

        public async Task<CcbSandboxExecutionResult> ExecuteAsync(
            CcbSandboxExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            CcbSandboxEvidence.ValidateRequest(request);
            Assert.True(File.Exists(Path.Combine(request.WorkspacePath, request.Entrypoint)));
            Assert.Equal(
                request.WorkspaceSha256,
                await CcbSandboxEvidence.ComputeWorkspaceSha256Async(request.WorkspacePath, cancellationToken));
            ExecutionCount++;
            var input = Encoding.UTF8.GetString(request.Input.Span);
            var output = Encoding.UTF8.GetBytes(outputMismatch ? "wrong" : input.ToUpperInvariant());
            return CcbSandboxEvidence.Create(
                request, LauncherSha256, BackendSha256, RuntimeSha256, output);
        }

        private static string Digest(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
