using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

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
        var pending = Assert.Single(await governance.GetPendingReviewItemsAsync());
        Assert.Equal(prepared.SubmissionId, pending.SubmissionId);
        Assert.Equal(
            ContributionCodec.CandidateSha256(candidate),
            ContributionCodec.CandidateSha256(pending.Candidate));
        Assert.Empty(pending.FindingCodes);
        var release = await governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now);
        var approved = await governance.GetReviewItemAsync(prepared.SubmissionId);
        Assert.Equal(ContributionState.Approved, approved.State);
        Assert.Equal("reviewer:fixture", approved.ReviewerId);
        Assert.Equal(release.Sequence, approved.ReleaseSequence);

        var client = await GlobalReplicaClient.OpenAsync(Path.Combine(root, "client"), registry.TrustRootSubjectPublicKeyInfo);
        await client.SynchronizeAsync(new RepositoryRegistryTransport(registry));
        Assert.Equal(release.Sha256, (await client.GetStatusAsync()).ReleaseSha256);
        Assert.Equal("resource:approved", Assert.Single(await client.SearchAsync("keyboard accessibility", null)).ResourceId);

        var wrongOwner = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.WithdrawOwnedAsync(prepared.SubmissionId, "contributor:other", now.AddDays(1)));
        Assert.Equal("submission_owner_mismatch", wrongOwner.Code);
        await governance.WithdrawOwnedAsync(
            prepared.SubmissionId, candidate.ContributorPseudonym, now.AddDays(1));
        await client.SynchronizeAsync(new RepositoryRegistryTransport(registry));
        Assert.True(Assert.Single(await client.SearchAsync("keyboard accessibility", null)).Deprecated);
        Assert.Equal(ContributionState.Withdrawn,
            (await governance.GetReviewItemAsync(prepared.SubmissionId)).State);
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
    public async Task ApprovalAndWithdrawal_AutomaticallyRecoverDurableIntentsAcrossRestart()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "automatic-recovery-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "automatic-recovery-governance");
        var approvalFault = new ThrowOncePublicationFaultInjector(
            ContributionPublicationCheckpoint.RegistryCommitted);
        var crashingApproval = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, approvalFault);
        var candidate = Candidate(
            "candidate:automatic-recovery",
            "resource:automatic-recovery",
            "A reviewer-authorized fixture that verifies automatic publication recovery after restart.");
        var prepared = Prepare(candidate, "nonce-automatic-recovery");
        await crashingApproval.IntakeAsync(prepared, now);

        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashingApproval.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));
        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);

        await ExpirePublicationLeasesAsync(governanceRoot);
        var recoveredApproval = await ContributionGovernanceService.OpenAsync(governanceRoot, registry);
        Assert.Equal(0, await recoveredApproval.RecoverPendingPublicationsAsync());
        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);

        var withdrawalFault = new ThrowOncePublicationFaultInjector(
            ContributionPublicationCheckpoint.IntentStored);
        var crashingWithdrawal = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, withdrawalFault);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashingWithdrawal.WithdrawAsync(prepared.SubmissionId, now.AddDays(1)));
        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);

        var recoveredWithdrawal = await ContributionGovernanceService.OpenAsync(governanceRoot, registry);
        Assert.Equal(0, await recoveredWithdrawal.RecoverPendingPublicationsAsync());
        Assert.Equal(2, (await registry.GetLatestAsync())!.Sequence);
        var (state, completedIntents) = await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId);
        Assert.Equal(ContributionState.Withdrawn, state);
        Assert.Equal(2L, completedIntents);
    }

    [Fact]
    public async Task ConcurrentRecovery_ClaimsReviewerIntentOnceAcrossServiceInstances()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        var registryRoot = Path.Combine(root, "concurrent-lease-registry");
        await using var registryOne = await GlobalRegistryRepository.OpenAsync(registryRoot, privateKey);
        await using var registryTwo = await GlobalRegistryRepository.OpenAsync(registryRoot, privateKey);
        var governanceRoot = Path.Combine(root, "concurrent-lease-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registryOne, null, fault);
        var candidate = Candidate(
            "candidate:concurrent-lease",
            "resource:concurrent-lease",
            "A durable reviewer intent claimed once across concurrent governance service instances.");
        var prepared = Prepare(candidate, "nonce-concurrent-lease-123");
        await crashing.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        var services = await Task.WhenAll(
            ContributionGovernanceService.OpenAsync(governanceRoot, registryOne),
            ContributionGovernanceService.OpenAsync(governanceRoot, registryTwo));

        Assert.Equal(0, await services[0].RecoverPendingPublicationsAsync());
        Assert.Equal(0, await services[1].RecoverPendingPublicationsAsync());
        Assert.Equal(1, (await registryOne.GetLatestAsync())!.Sequence);
        var (state, completedIntents) = await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId);
        var (attempts, activeLeases) = await ReadPublicationLeaseStateAsync(governanceRoot);
        Assert.Equal(ContributionState.Approved, state);
        Assert.Equal(1L, completedIntents);
        Assert.Equal(1L, attempts);
        Assert.Equal(0L, activeLeases);
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimedAndFencesTheOriginalWorkerCompletion()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "lease-fencing-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "lease-fencing-governance");
        var fault = new BlockingPublicationFaultInjector(ContributionPublicationCheckpoint.RegistryCommitted);
        var first = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:lease-fencing",
            "resource:lease-fencing",
            "An expired publication lease is reclaimed without granting new reviewer authority.");
        var prepared = Prepare(candidate, "nonce-lease-fencing-12345");
        await first.IntakeAsync(prepared, now);

        var firstPublication = first.ApproveAndPublishAsync(
            prepared.SubmissionId, "reviewer:fixture", now);
        await fault.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await ContributionGovernanceService.OpenAsync(governanceRoot, registry);
        Assert.Equal(0, await second.RecoverPendingPublicationsAsync());
        Assert.Equal(ContributionState.Quarantined,
            (await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId)).State);

        await ExpirePublicationLeasesAsync(governanceRoot);
        Assert.Equal(1, await second.RecoverPendingPublicationsAsync());
        fault.Release();
        var lost = await Assert.ThrowsAsync<ContributionPolicyException>(async () => await firstPublication);

        Assert.Equal("publication_lease_lost", lost.Code);
        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);
        var (state, completedIntents) = await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId);
        var (attempts, activeLeases) = await ReadPublicationLeaseStateAsync(governanceRoot);
        Assert.Equal(ContributionState.Approved, state);
        Assert.Equal(1L, completedIntents);
        Assert.Equal(2L, attempts);
        Assert.Equal(0L, activeLeases);
    }

    [Fact]
    public async Task AutomaticRecovery_RejectsMalformedPublicationLease()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "lease-tamper-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "lease-tamper-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.RegistryCommitted);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:lease-tamper",
            "resource:lease-tamper",
            "A malformed publication worker lease must fail closed during automatic recovery.");
        var prepared = Prepare(candidate, "nonce-lease-tamper-123456");
        await crashing.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        await UpdateGovernanceAsync(
            governanceRoot,
            """
            UPDATE publication_intent_leases
            SET lease_token = 'invalid-token',
                acquired_utc = '2000-01-01T00:00:00.0000000+00:00',
                expires_utc = '2000-01-01T00:01:00.0000000+00:00';
            """);
        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            ContributionGovernanceService.OpenAsync(governanceRoot, registry));

        Assert.Equal("publication_lease_corrupt", exception.Code);
        Assert.Equal(ContributionState.Quarantined,
            (await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId)).State);
    }

    [Fact]
    public async Task AutomaticRecovery_StopsAtBoundedPublicationAttemptLimit()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "lease-attempt-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "lease-attempt-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:lease-attempt-limit",
            "resource:lease-attempt-limit",
            "A bounded retry fixture prevents an authorized intent from looping forever.");
        var prepared = Prepare(candidate, "nonce-lease-attempt-limit");
        await crashing.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        await using (var connection = await OpenGovernanceConnectionAsync(governanceRoot, readOnly: false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO publication_intent_leases(
                    operation_id, lease_owner, lease_token, acquired_utc, expires_utc, attempt_count)
                VALUES($operation, 'worker-fixture', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                       '2000-01-01T00:00:00.0000000+00:00',
                       '2000-01-01T00:01:00.0000000+00:00', 20);
                """;
            command.Parameters.AddWithValue(
                "$operation", PublicationOperationId("approval", prepared.SubmissionId));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            ContributionGovernanceService.OpenAsync(governanceRoot, registry));

        Assert.Equal("publication_lease_attempt_limit", exception.Code);
        Assert.Null(await registry.GetLatestAsync());
        Assert.Equal(ContributionState.Quarantined,
            (await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId)).State);
    }

    [Fact]
    public async Task HostedWorker_ContinuouslyCompletesAuthorizedPendingIntent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "hosted-worker-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "hosted-worker-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var governance = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:hosted-worker",
            "resource:hosted-worker",
            "A durable worker fixture completed without expanding the reviewer's authority.");
        var prepared = Prepare(candidate, "nonce-hosted-worker-12345");
        await governance.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        governance = await ContributionGovernanceService.OpenForContinuousWorkerAsync(
            governanceRoot, registry);

        var worker = new ContributionPublicationWorker(
            governance,
            new SimulationContributionWorkerOptions(TimeSpan.FromMilliseconds(100)),
            NullLogger<ContributionPublicationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await governance.GetReviewItemAsync(prepared.SubmissionId, timeout.Token)).State !=
                   ContributionState.Approved)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);
        Assert.Empty(await governance.GetPublicationQueueAsync());
        var (state, completedIntents) = await ReadRecoveryStateAsync(
            governanceRoot, prepared.SubmissionId);
        Assert.Equal(ContributionState.Approved, state);
        Assert.Equal(1L, completedIntents);
    }

    [Fact]
    public async Task Worker_BackoffDeadLetterAndExplicitRetryRemainBodyFree()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "worker-dead-letter-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "worker-dead-letter-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var governance = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        const string privateBody = "Private reviewed body that must never enter the worker queue.";
        var candidate = Candidate(
            "candidate:worker-dead-letter",
            "resource:worker-dead-letter",
            privateBody);
        var prepared = Prepare(candidate, "nonce-worker-dead-letter");
        await governance.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            governance.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        var operationId = PublicationOperationId("approval", prepared.SubmissionId);
        var conflicting = new GlobalResourceRecord(
            "resource:conflicting-operation",
            "1",
            KnowledgeResourceKind.Ckb,
            "Conflicting fixture",
            "A different canonical change bound to the stolen operation identifier.",
            "https://example.test/conflict",
            "CC-BY-4.0",
            []);
        await registry.PublishIdempotentAsync(
            operationId,
            [new GlobalReleaseChange(
                GlobalReleaseChangeKind.Upsert,
                conflicting.ResourceId,
                conflicting.Version,
                conflicting)],
            now);

        governance = await ContributionGovernanceService.OpenForContinuousWorkerAsync(
            governanceRoot, registry);

        var workerTime = now.AddHours(1);
        var first = await governance.ProcessReadyPublicationIntentsAsync(workerTime);
        Assert.Equal(new ContributionPublicationWorkerResult(0, 1, 0), first);
        var queued = Assert.Single(await governance.GetPublicationQueueAsync());
        Assert.Equal("registry_operation_failed", queued.FailureCode);
        Assert.Equal(1, queued.ConsecutiveFailures);
        Assert.NotNull(queued.NextAttemptUtc);
        Assert.Null(queued.DeadLetteredUtc);
        Assert.DoesNotContain(privateBody, JsonSerializer.Serialize(queued), StringComparison.Ordinal);

        Assert.Equal(
            new ContributionPublicationWorkerResult(0, 0, 0),
            await governance.ProcessReadyPublicationIntentsAsync(workerTime));
        for (var expectedFailures = 2; expectedFailures <= 5; expectedFailures++)
        {
            queued = Assert.Single(await governance.GetPublicationQueueAsync());
            workerTime = queued.NextAttemptUtc!.Value;
            var result = await governance.ProcessReadyPublicationIntentsAsync(workerTime);
            Assert.Equal(0, result.Completed);
            Assert.Equal(1, result.Failed);
            Assert.Equal(expectedFailures == 5 ? 1 : 0, result.DeadLettered);
        }

        queued = Assert.Single(await governance.GetPublicationQueueAsync());
        Assert.Equal(operationId, queued.OperationId);
        Assert.Equal(prepared.SubmissionId, queued.SubmissionId);
        Assert.Equal("approval", queued.Action);
        Assert.Equal(5, queued.ConsecutiveFailures);
        Assert.Null(queued.NextAttemptUtc);
        Assert.NotNull(queued.DeadLetteredUtc);

        var retryTime = workerTime.AddMinutes(1);
        queued = await governance.RetryDeadLetterAsync(operationId, retryTime);
        Assert.Equal(0, queued.ConsecutiveFailures);
        Assert.Equal(1, queued.RemediationCount);
        Assert.Equal(retryTime, queued.NextAttemptUtc);
        Assert.Null(queued.DeadLetteredUtc);
        Assert.Equal(
            new ContributionPublicationWorkerResult(0, 1, 0),
            await governance.ProcessReadyPublicationIntentsAsync(retryTime));
        Assert.Equal(1, (await registry.GetLatestAsync())!.Sequence);
        Assert.Equal(ContributionState.Quarantined,
            (await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId)).State);

        await UpdateGovernanceAsync(
            governanceRoot,
            """
            PRAGMA ignore_check_constraints = ON;
            UPDATE publication_intent_failures
            SET failure_code = 'INVALID persisted failure code';
            """);
        var corrupt = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            governance.GetPublicationQueueAsync());
        Assert.Equal("publication_queue_corrupt", corrupt.Code);
    }

    [Fact]
    public async Task CcbApproval_RecoversAfterValidatedIntentWithoutRerunningUntrustedCode()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "ccb-recovery-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "ccb-recovery-governance");
        var executor = new FixtureSandboxExecutor();
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, executor, fault);
        var artifact = CcbContributionPackageTests.CreatePackage();
        var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact));
        var candidate = CcbCandidate("candidate:ccb-recovery", "tool:community-upper", artifactSha256);
        var prepared = Prepare(candidate, "nonce-ccb-recovery-1234");
        await crashing.IntakeAsync(prepared, artifact, now);

        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));
        Assert.Null(await registry.GetLatestAsync());
        Assert.Equal(2, executor.ExecutionCount);

        var recovered = await ContributionGovernanceService.OpenAsync(governanceRoot, registry);
        Assert.Equal(0, await recovered.RecoverPendingPublicationsAsync());
        Assert.Equal(2, executor.ExecutionCount);
        var transport = new RepositoryRegistryTransport(registry);
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "ccb-recovery-client"), registry.TrustRootSubjectPublicKeyInfo);
        await client.SynchronizeAsync(transport);
        var resource = Assert.Single(await client.SearchAsync("community upper", null));
        var reference = Assert.IsType<GlobalArtifactReference>(resource.Artifact);
        Assert.Equal(artifact, await File.ReadAllBytesAsync(
            await client.GetArtifactAsync(reference.Sha256, transport)));
        var (state, completedIntents) = await ReadRecoveryStateAsync(governanceRoot, prepared.SubmissionId);
        Assert.Equal(ContributionState.Approved, state);
        Assert.Equal(1L, completedIntents);
    }

    [Fact]
    public async Task CcbRecovery_RejectsSameLengthAuthoritativeArtifactTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registryRoot = Path.Combine(root, "ccb-recovery-tamper-registry");
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            registryRoot, key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "ccb-recovery-tamper-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, new FixtureSandboxExecutor(), fault);
        var artifact = CcbContributionPackageTests.CreatePackage();
        var artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact));
        var candidate = CcbCandidate("candidate:ccb-recovery-tamper", "tool:community-upper", artifactSha256);
        var prepared = Prepare(candidate, "nonce-ccb-recovery-tamper");
        await crashing.IntakeAsync(prepared, artifact, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        var authoritativePath = Path.Combine(
            registryRoot, "artifacts", "sha256", artifactSha256[..2], artifactSha256);
        var changed = await File.ReadAllBytesAsync(authoritativePath);
        changed[0] ^= 0x01;
        await File.WriteAllBytesAsync(authoritativePath, changed);

        var exception = await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            ContributionGovernanceService.OpenAsync(governanceRoot, registry));
        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await registry.GetLatestAsync());
    }

    [Fact]
    public async Task AutomaticRecovery_RejectsTamperedPublicationIntent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "tampered-intent-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "tampered-intent-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:tampered-intent",
            "resource:tampered-intent",
            "A deterministic publication-intent corruption denial fixture for startup recovery.");
        var prepared = Prepare(candidate, "nonce-tampered-intent-123");
        await crashing.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = Path.Combine(governanceRoot, "contribution-governance.sqlite3"),
                         Mode = SqliteOpenMode.ReadWrite,
                         Pooling = false,
                     }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE publication_intents SET request_sha256 = $sha WHERE submission_id = $submission;";
            command.Parameters.AddWithValue("$sha", new string('0', 64));
            command.Parameters.AddWithValue("$submission", prepared.SubmissionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            ContributionGovernanceService.OpenAsync(governanceRoot, registry));
        Assert.Equal("publication_intent_binding_invalid", exception.Code);
        Assert.Null(await registry.GetLatestAsync());
    }

    [Fact]
    public async Task AutomaticRecovery_RejectsCandidateChangedAfterReviewerIntent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var registry = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "tampered-candidate-registry"), key.ExportPkcs8PrivateKey());
        var governanceRoot = Path.Combine(root, "tampered-candidate-governance");
        var fault = new ThrowOncePublicationFaultInjector(ContributionPublicationCheckpoint.IntentStored);
        var crashing = await ContributionGovernanceService.OpenForTestingAsync(
            governanceRoot, registry, null, fault);
        var candidate = Candidate(
            "candidate:tampered-after-review",
            "resource:tampered-after-review",
            "A digest-bound candidate that must not change after its reviewer intent is persisted.");
        var prepared = Prepare(candidate, "nonce-tampered-after-review");
        await crashing.IntakeAsync(prepared, now);
        await Assert.ThrowsAsync<SimulatedPublicationCrashException>(() =>
            crashing.ApproveAndPublishAsync(prepared.SubmissionId, "reviewer:fixture", now));

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = Path.Combine(governanceRoot, "contribution-governance.sqlite3"),
                         Mode = SqliteOpenMode.ReadWrite,
                         Pooling = false,
                     }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE contributions SET candidate_json = $candidate WHERE submission_id = $submission;";
            command.Parameters.AddWithValue(
                "$candidate",
                JsonSerializer.Serialize(candidate with { Body = "Changed after review." }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$submission", prepared.SubmissionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<ContributionPolicyException>(() =>
            ContributionGovernanceService.OpenAsync(governanceRoot, registry));
        Assert.Equal("submission_corrupt", exception.Code);
        Assert.Null(await registry.GetLatestAsync());
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

    private static async Task<(ContributionState State, long CompletedIntents)> ReadRecoveryStateAsync(
        string governanceRoot,
        string submissionId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(governanceRoot, "contribution-governance.sqlite3"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM contributions WHERE submission_id = $submission;";
        command.Parameters.AddWithValue("$submission", submissionId);
        var state = (ContributionState)Convert.ToInt32(await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM publication_intents WHERE submission_id = $submission AND completed = 1;";
        command.Parameters.AddWithValue("$submission", submissionId);
        return (state, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<(long Attempts, long ActiveLeases)> ReadPublicationLeaseStateAsync(
        string governanceRoot)
    {
        await using var connection = await OpenGovernanceConnectionAsync(governanceRoot);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(attempt_count), SUM(CASE WHEN lease_token IS NULL THEN 0 ELSE 1 END) FROM publication_intent_leases;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static Task ExpirePublicationLeasesAsync(string governanceRoot) =>
        UpdateGovernanceAsync(
            governanceRoot,
            """
            UPDATE publication_intent_leases
            SET acquired_utc = '2000-01-01T00:00:00.0000000+00:00',
                expires_utc = '2000-01-01T00:01:00.0000000+00:00'
            WHERE lease_token IS NOT NULL;
            """);

    private static async Task UpdateGovernanceAsync(string governanceRoot, string statement)
    {
        await using var connection = await OpenGovernanceConnectionAsync(governanceRoot, readOnly: false);
        var command = connection.CreateCommand();
        command.CommandText = statement;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<SqliteConnection> OpenGovernanceConnectionAsync(
        string governanceRoot,
        bool readOnly = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(governanceRoot, "contribution-governance.sqlite3"),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class ThrowOncePublicationFaultInjector(
        ContributionPublicationCheckpoint target) : IContributionPublicationFaultInjector
    {
        private bool thrown;

        public Task OnCheckpointAsync(
            ContributionPublicationCheckpoint checkpoint,
            string operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(operationId));
            if (!thrown && checkpoint == target)
            {
                thrown = true;
                throw new SimulatedPublicationCrashException();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SimulatedPublicationCrashException : Exception;

    private sealed class BlockingPublicationFaultInjector(
        ContributionPublicationCheckpoint target) : IContributionPublicationFaultInjector
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => reached.Task;

        public void Release() => released.TrySetResult();

        public async Task OnCheckpointAsync(
            ContributionPublicationCheckpoint checkpoint,
            string operationId,
            CancellationToken cancellationToken)
        {
            Assert.False(string.IsNullOrWhiteSpace(operationId));
            if (checkpoint != target) return;
            reached.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
        }
    }

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
