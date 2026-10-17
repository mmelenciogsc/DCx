using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record AuthoritativeUsageContext(
    bool EntitlementActive,
    string ActorPseudonym,
    byte[] InstallationIntegrityKey,
    DateTimeOffset NowUtc);

public sealed class ContributionGovernanceService
{
    private const int MaximumRecoveryBatch = 100;
    private const int MaximumPublicationAttempts = 20;
    private const int MaximumPublicationChangeJsonBytes = 256 * 1024;
    private static readonly TimeSpan PublicationLeaseDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly string connectionString;
    private readonly string quarantineRoot;
    private readonly string quarantineArtifactsRoot;
    private readonly GlobalRegistryRepository registry;
    private readonly ICcbSandboxExecutor? ccbSandboxExecutor;
    private readonly IContributionPublicationFaultInjector? publicationFaultInjector;
    private readonly ContributionScanner scanner = new();
    private readonly CcbPackageValidator ccbPackageValidator = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private readonly string workerId = $"worker-{Guid.NewGuid():N}";

    private ContributionGovernanceService(
        string root,
        GlobalRegistryRepository registry,
        ICcbSandboxExecutor? ccbSandboxExecutor,
        IContributionPublicationFaultInjector? publicationFaultInjector)
    {
        this.registry = registry;
        this.ccbSandboxExecutor = ccbSandboxExecutor;
        this.publicationFaultInjector = publicationFaultInjector;
        quarantineRoot = Path.Combine(root, "quarantine");
        quarantineArtifactsRoot = Path.Combine(quarantineRoot, "artifacts", "sha256");
        Directory.CreateDirectory(quarantineArtifactsRoot);
        Directory.CreateDirectory(Path.Combine(quarantineRoot, "workspaces"));
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "contribution-governance.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public static async Task<ContributionGovernanceService> OpenAsync(
        string root,
        GlobalRegistryRepository registry,
        ICcbSandboxExecutor? ccbSandboxExecutor = null,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(root, registry, ccbSandboxExecutor, null, cancellationToken).ConfigureAwait(false);

    internal static async Task<ContributionGovernanceService> OpenForTestingAsync(
        string root,
        GlobalRegistryRepository registry,
        ICcbSandboxExecutor? ccbSandboxExecutor,
        IContributionPublicationFaultInjector publicationFaultInjector,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(
            root, registry, ccbSandboxExecutor, publicationFaultInjector, cancellationToken).ConfigureAwait(false);

    private static async Task<ContributionGovernanceService> OpenCoreAsync(
        string root,
        GlobalRegistryRepository registry,
        ICcbSandboxExecutor? ccbSandboxExecutor,
        IContributionPublicationFaultInjector? publicationFaultInjector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(registry);
        var canonical = Path.GetFullPath(root);
        Directory.CreateDirectory(canonical);
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ContributionPolicyException("governance_root_link_denied");
        }

        var service = new ContributionGovernanceService(
            canonical, registry, ccbSandboxExecutor, publicationFaultInjector);
        await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await service.RecoverPendingPublicationsAsync(cancellationToken).ConfigureAwait(false);
        return service;
    }

    public async Task<ContributionState> IntakeAsync(
        PreparedContribution submission,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await IntakeAsync(submission, ReadOnlyMemory<byte>.Empty, now, cancellationToken).ConfigureAwait(false);

    public async Task<ContributionState> IntakeAsync(
        PreparedContribution submission,
        ReadOnlyMemory<byte> artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        _ = new ContributionPreparationService(scanner).Prepare(
            new ContributionPreferences(true),
            submission.Candidate,
            submission.Attestation,
            submission.Approval,
            now);
        if (submission.State != ContributionState.Quarantined ||
            string.IsNullOrWhiteSpace(submission.SubmissionId) || submission.SubmissionId.Length > 128)
        {
            throw new ContributionPolicyException("submission_schema_invalid");
        }

        ContributionLineageGraph.Validate(new Dictionary<string, IReadOnlyList<ContributionCreditEdge>>(StringComparer.Ordinal)
        {
            [submission.Candidate.ResourceId] = submission.Candidate.DerivedFrom,
        });
        var serverScan = scanner.Scan(submission.Candidate);
        if (submission.Candidate.Kind == KnowledgeResourceKind.Ccb)
        {
            var package = await ValidateAndStageCcbArtifactAsync(submission.Candidate, artifact, cancellationToken)
                .ConfigureAwait(false);
            serverScan = MergeScans(
                serverScan,
                scanner.Scan(submission.Candidate with
                {
                    Body = $"{submission.Candidate.Body}\n{package.ReviewText}",
                }));
        }
        else if (!artifact.IsEmpty)
        {
            throw new ContributionPolicyException("ckb_artifact_unexpected");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadSubmissionAsync(connection, submission.SubmissionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.CandidateSha256, ContributionCodec.CandidateSha256(submission.Candidate), StringComparison.OrdinalIgnoreCase))
                {
                    throw new ContributionPolicyException("submission_id_conflict");
                }

                return existing.State;
            }

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO contributions(
                    submission_id, candidate_id, candidate_sha256, approval_nonce, resource_id, version,
                    candidate_json, scan_codes, state, contributor_pseudonym, created_utc)
                VALUES($submission, $candidate, $sha, $nonce, $resource, $version,
                       $json, $scan, $state, $contributor, $created);
                """;
            command.Parameters.AddWithValue("$submission", submission.SubmissionId);
            command.Parameters.AddWithValue("$candidate", submission.Candidate.CandidateId);
            command.Parameters.AddWithValue("$sha", ContributionCodec.CandidateSha256(submission.Candidate));
            command.Parameters.AddWithValue("$nonce", submission.Approval.ApprovalNonce);
            command.Parameters.AddWithValue("$resource", submission.Candidate.ResourceId);
            command.Parameters.AddWithValue("$version", submission.Candidate.Version);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(submission.Candidate, JsonOptions));
            command.Parameters.AddWithValue("$scan", string.Join(',', serverScan.Findings.Select(item => item.Code)));
            command.Parameters.AddWithValue("$state", (int)ContributionState.Quarantined);
            command.Parameters.AddWithValue("$contributor", submission.Candidate.ContributorPseudonym);
            command.Parameters.AddWithValue("$created", now.ToUniversalTime().ToString("O"));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new ContributionPolicyException("approval_nonce_replay");
            }

            return ContributionState.Quarantined;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<GlobalReleaseDescriptor> ApproveAndPublishAsync(
        string submissionId,
        string reviewerId,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reviewerId) || reviewerId.Length > 128)
        {
            throw new ContributionPolicyException("reviewer_invalid");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await ReadSubmissionAsync(connection, submissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ContributionPolicyException("submission_missing");
            if (row.State != ContributionState.Quarantined)
            {
                throw new ContributionPolicyException("submission_not_quarantined");
            }

            var candidate = DecodeCandidate(row);
            var scan = scanner.Scan(candidate);
            if (!scan.IsEligible)
            {
                throw new ContributionPolicyException("quarantine_findings_present");
            }

            GlobalArtifactReference? artifact = null;
            if (candidate.Kind == KnowledgeResourceKind.Ccb)
            {
                artifact = await ValidateCcbForPublicationAsync(
                    connection,
                    submissionId,
                    reviewerId,
                    candidate,
                    issuedUtc,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (candidate.Kind != KnowledgeResourceKind.Ckb)
            {
                throw new ContributionPolicyException("submission_kind_invalid");
            }

            var resource = new GlobalResourceRecord(
                candidate.ResourceId,
                candidate.Version,
                candidate.Kind,
                candidate.Title,
                candidate.Body,
                candidate.SourceUri,
                candidate.License,
                candidate.Capabilities,
                Artifact: artifact);
            var change = new GlobalReleaseChange(
                GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource);
            var intent = await StorePublicationIntentAsync(
                connection,
                submissionId,
                PublicationIntentAction.Approval,
                change,
                reviewerId,
                issuedUtc,
                cancellationToken).ConfigureAwait(false);
            await NotifyCheckpointAsync(
                ContributionPublicationCheckpoint.IntentStored, intent.OperationId, cancellationToken).ConfigureAwait(false);
            return await PublishClaimedIntentAsync(connection, intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<GlobalReleaseDescriptor> WithdrawAsync(
        string submissionId,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await ReadSubmissionAsync(connection, submissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ContributionPolicyException("submission_missing");
            if (row.State != ContributionState.Approved)
            {
                throw new ContributionPolicyException("submission_not_approved");
            }

            var candidate = DecodeCandidate(row);
            var change = new GlobalReleaseChange(
                GlobalReleaseChangeKind.Deprecate, candidate.ResourceId, candidate.Version);
            var intent = await StorePublicationIntentAsync(
                connection,
                submissionId,
                PublicationIntentAction.Withdrawal,
                change,
                reviewerId: null,
                issuedUtc,
                cancellationToken).ConfigureAwait(false);
            await NotifyCheckpointAsync(
                ContributionPublicationCheckpoint.IntentStored, intent.OperationId, cancellationToken).ConfigureAwait(false);
            return await PublishClaimedIntentAsync(connection, intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<int> RecoverPendingPublicationsAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var intents = await ReadPendingPublicationIntentsAsync(connection, cancellationToken).ConfigureAwait(false);
            var processed = 0;
            foreach (var intent in intents)
            {
                var lease = await TryClaimPublicationIntentAsync(connection, intent, cancellationToken)
                    .ConfigureAwait(false);
                if (lease is null) continue;
                try
                {
                    await ValidatePublicationIntentAsync(connection, intent, cancellationToken).ConfigureAwait(false);
                    await RenewPublicationLeaseAsync(connection, lease, cancellationToken).ConfigureAwait(false);
                    var release = await registry.PublishIdempotentAsync(
                        intent.OperationId,
                        [intent.Change],
                        intent.RequestedUtc,
                        cancellationToken).ConfigureAwait(false);
                    await RenewPublicationLeaseAsync(connection, lease, cancellationToken).ConfigureAwait(false);
                    await CompletePublicationIntentAsync(
                        connection, intent, lease, release, cancellationToken).ConfigureAwait(false);
                    processed++;
                }
                catch
                {
                    await TryReleasePublicationLeaseAsync(connection, lease).ConfigureAwait(false);
                    throw;
                }
            }

            return processed;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<UsageReceiptDecision> EvaluateUsageAsync(
        QualifiedUsageReceipt receipt,
        string integritySignature,
        AuthoritativeUsageContext authoritative,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(authoritative);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var replay = await ReceiptExistsAsync(connection, receipt, cancellationToken).ConfigureAwait(false);
            var contribution = await ReadApprovedResourceAsync(connection, receipt.ResourceId, receipt.ResourceVersion, cancellationToken)
                .ConfigureAwait(false);
            var prior = await CountQualifiedForDayAsync(connection, receipt, cancellationToken).ConfigureAwait(false);
            var context = new UsageReceiptContext(
                authoritative.EntitlementActive,
                contribution is not null,
                contribution is not null && contribution.ContributorPseudonym == authoritative.ActorPseudonym,
                prior,
                replay.Receipt,
                replay.Nonce,
                authoritative.NowUtc);
            if (!UsageReceiptPolicy.Verify(receipt, integritySignature, authoritative.InstallationIntegrityKey))
            {
                return new UsageReceiptDecision(false, "integrity_invalid");
            }

            var decision = UsageReceiptPolicy.Evaluate(receipt, context);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO usage_receipts(
                    receipt_id, nonce, installation_pseudonym, entitlement_id, resource_id, resource_version,
                    occurred_utc, qualified, decision_code)
                VALUES($receipt, $nonce, $installation, $entitlement, $resource, $version, $occurred, $qualified, $decision);
                """;
            command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
            command.Parameters.AddWithValue("$nonce", receipt.Nonce);
            command.Parameters.AddWithValue("$installation", receipt.InstallationPseudonym);
            command.Parameters.AddWithValue("$entitlement", receipt.EntitlementId);
            command.Parameters.AddWithValue("$resource", receipt.ResourceId);
            command.Parameters.AddWithValue("$version", receipt.ResourceVersion);
            command.Parameters.AddWithValue("$occurred", receipt.OccurredUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$qualified", decision.Qualified ? 1 : 0);
            command.Parameters.AddWithValue("$decision", decision.Code);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return decision;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS contributions(
                submission_id TEXT PRIMARY KEY,
                candidate_id TEXT NOT NULL,
                candidate_sha256 TEXT NOT NULL,
                approval_nonce TEXT NOT NULL UNIQUE,
                resource_id TEXT NOT NULL,
                version TEXT NOT NULL,
                candidate_json TEXT NOT NULL,
                scan_codes TEXT NOT NULL,
                state INTEGER NOT NULL,
                contributor_pseudonym TEXT NOT NULL,
                reviewer_id TEXT,
                release_sequence INTEGER,
                created_utc TEXT NOT NULL,
                reviewed_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS usage_receipts(
                receipt_id TEXT PRIMARY KEY,
                nonce TEXT NOT NULL UNIQUE,
                installation_pseudonym TEXT NOT NULL,
                entitlement_id TEXT NOT NULL,
                resource_id TEXT NOT NULL,
                resource_version TEXT NOT NULL,
                occurred_utc TEXT NOT NULL,
                qualified INTEGER NOT NULL,
                decision_code TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ccb_sandbox_reviews(
                submission_id TEXT NOT NULL,
                test_name TEXT NOT NULL,
                evidence_sha256 TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                workspace_sha256 TEXT NOT NULL,
                test_suite_sha256 TEXT NOT NULL,
                launcher_sha256 TEXT NOT NULL,
                backend_sha256 TEXT NOT NULL,
                runtime_sha256 TEXT NOT NULL,
                output_sha256 TEXT,
                passed INTEGER NOT NULL,
                failure_code TEXT,
                reviewer_id TEXT NOT NULL,
                reviewed_utc TEXT NOT NULL,
                PRIMARY KEY(submission_id, test_name, evidence_sha256)
            );
            CREATE TABLE IF NOT EXISTS publication_intents(
                operation_id TEXT PRIMARY KEY,
                submission_id TEXT NOT NULL,
                action INTEGER NOT NULL,
                request_sha256 TEXT NOT NULL,
                change_json TEXT NOT NULL,
                reviewer_id TEXT,
                requested_utc TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0,
                release_sequence INTEGER,
                completed_utc TEXT,
                UNIQUE(submission_id, action),
                FOREIGN KEY(submission_id) REFERENCES contributions(submission_id)
            );
            CREATE TABLE IF NOT EXISTS publication_intent_leases(
                operation_id TEXT PRIMARY KEY,
                lease_owner TEXT,
                lease_token TEXT UNIQUE,
                acquired_utc TEXT,
                expires_utc TEXT,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(operation_id) REFERENCES publication_intents(operation_id) ON DELETE CASCADE,
                CHECK(attempt_count >= 0 AND attempt_count <= 20),
                CHECK(
                    (lease_owner IS NULL AND lease_token IS NULL AND acquired_utc IS NULL AND expires_utc IS NULL) OR
                    (lease_owner IS NOT NULL AND lease_token IS NOT NULL AND acquired_utc IS NOT NULL AND expires_utc IS NOT NULL)
                )
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<CcbContributionPackage> ValidateAndStageCcbArtifactAsync(
        ContributionCandidate candidate,
        ReadOnlyMemory<byte> artifact,
        CancellationToken cancellationToken)
    {
        if (artifact.IsEmpty || artifact.Length > ContentAddressedArtifactStore.MaximumArtifactBytes ||
            string.IsNullOrWhiteSpace(candidate.ArtifactSha256) || candidate.ArtifactSha256.Length != 64 ||
            !candidate.ArtifactSha256.All(char.IsAsciiHexDigit))
        {
            throw new ContributionPolicyException("ccb_artifact_required");
        }

        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact.Span));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256),
                Convert.FromHexString(candidate.ArtifactSha256)))
        {
            throw new ContributionPolicyException("ccb_artifact_digest_invalid");
        }

        CcbContributionPackage package;
        try
        {
            package = ccbPackageValidator.ReadContributionPackage(artifact);
        }
        catch (KnowledgeArtifactException)
        {
            throw new ContributionPolicyException("ccb_artifact_policy_invalid");
        }

        if (!string.Equals(package.Manifest.Id, candidate.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(package.Manifest.Version, candidate.Version, StringComparison.Ordinal) ||
            !package.LicenseText.Contains(candidate.License, StringComparison.OrdinalIgnoreCase) ||
            !package.DeclaredLicenses.Contains(candidate.License, StringComparer.OrdinalIgnoreCase))
        {
            throw new ContributionPolicyException("ccb_artifact_metadata_mismatch");
        }

        var normalized = actualSha256.ToLowerInvariant();
        var directory = Path.Combine(quarantineArtifactsRoot, normalized[..2]);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{normalized}.zip");
        if (!File.Exists(destination))
        {
            var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, artifact.ToArray(), cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        _ = await ReadStagedArtifactAsync(normalized, cancellationToken).ConfigureAwait(false);
        return package;
    }

    private async Task<GlobalArtifactReference> ValidateCcbForPublicationAsync(
        SqliteConnection connection,
        string submissionId,
        string reviewerId,
        ContributionCandidate candidate,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken)
    {
        if (ccbSandboxExecutor is null)
        {
            throw new ContributionPolicyException("ccb_sandbox_review_required");
        }

        var artifactSha256 = candidate.ArtifactSha256
            ?? throw new ContributionPolicyException("ccb_artifact_required");
        var artifact = await ReadStagedArtifactAsync(artifactSha256, cancellationToken).ConfigureAwait(false);
        CcbContributionPackage package;
        try
        {
            package = ccbPackageValidator.ReadContributionPackage(artifact);
        }
        catch (KnowledgeArtifactException)
        {
            throw new ContributionPolicyException("ccb_artifact_policy_invalid");
        }

        if (!package.LicenseText.Contains(candidate.License, StringComparison.OrdinalIgnoreCase) ||
            !package.DeclaredLicenses.Contains(candidate.License, StringComparer.OrdinalIgnoreCase) ||
            !scanner.Scan(candidate with { Body = $"{candidate.Body}\n{package.ReviewText}" }).IsEligible)
        {
            throw new ContributionPolicyException("quarantine_findings_present");
        }

        var workspace = Path.Combine(quarantineRoot, "workspaces", Guid.NewGuid().ToString("N"));
        try
        {
            ccbPackageValidator.ExtractValidated(artifact, workspace);
            var workspaceSha256 = await CcbSandboxEvidence.ComputeWorkspaceSha256Async(workspace, cancellationToken)
                .ConfigureAwait(false);
            foreach (var test in package.Tests)
            {
                var request = new CcbSandboxExecutionRequest(
                    artifactSha256,
                    workspaceSha256,
                    package.TestSuiteSha256,
                    workspace,
                    package.Manifest.Entrypoint,
                    test.Input);
                var result = await ccbSandboxExecutor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                CcbSandboxEvidence.VerifyBoundResult(request, result);
                await StoreSandboxEvidenceAsync(
                    connection, submissionId, test.Name, reviewerId, issuedUtc, result, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Passed)
                {
                    throw new ContributionPolicyException("ccb_sandbox_test_failed");
                }

                if (!string.Equals(result.OutputSha256, test.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ContributionPolicyException("ccb_sandbox_output_mismatch");
                }
            }

            return await registry.AddArtifactAsync(
                artifact,
                "application/vnd.dcx.ccb+zip",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    private async Task<byte[]> ReadStagedArtifactAsync(string sha256, CancellationToken cancellationToken)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new ContributionPolicyException("ccb_artifact_digest_invalid");
        }

        var normalized = sha256.ToLowerInvariant();
        var path = Path.Combine(quarantineArtifactsRoot, normalized[..2], $"{normalized}.zip");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > ContentAddressedArtifactStore.MaximumArtifactBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ContributionPolicyException("ccb_artifact_missing_or_unsafe");
        }

        var content = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
        var actual = SHA256.HashData(content);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(normalized)))
        {
            throw new ContributionPolicyException("ccb_artifact_tampered");
        }

        return content;
    }

    private static async Task StoreSandboxEvidenceAsync(
        SqliteConnection connection,
        string submissionId,
        string testName,
        string reviewerId,
        DateTimeOffset reviewedUtc,
        CcbSandboxExecutionResult result,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO ccb_sandbox_reviews(
                submission_id, test_name, evidence_sha256, policy_version, artifact_sha256,
                workspace_sha256, test_suite_sha256, launcher_sha256, backend_sha256,
                runtime_sha256, output_sha256, passed, failure_code, reviewer_id, reviewed_utc)
            VALUES($submission, $test, $evidence, $policy, $artifact,
                   $workspace, $suite, $launcher, $backend,
                   $runtime, $output, $passed, $failure, $reviewer, $reviewed);
            """;
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$test", testName);
        command.Parameters.AddWithValue("$evidence", result.EvidenceSha256);
        command.Parameters.AddWithValue("$policy", result.PolicyVersion);
        command.Parameters.AddWithValue("$artifact", result.ArtifactSha256);
        command.Parameters.AddWithValue("$workspace", result.WorkspaceSha256);
        command.Parameters.AddWithValue("$suite", result.TestSuiteSha256);
        command.Parameters.AddWithValue("$launcher", result.LauncherSha256);
        command.Parameters.AddWithValue("$backend", result.BackendSha256);
        command.Parameters.AddWithValue("$runtime", result.RuntimeSha256);
        command.Parameters.AddWithValue("$output", (object?)result.OutputSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
        command.Parameters.AddWithValue("$failure", (object?)result.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewer", reviewerId);
        command.Parameters.AddWithValue("$reviewed", reviewedUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GlobalReleaseDescriptor> PublishClaimedIntentAsync(
        SqliteConnection connection,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        var lease = await TryClaimPublicationIntentAsync(connection, intent, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ContributionPolicyException("publication_intent_busy");
        try
        {
            await ValidatePublicationIntentAsync(connection, intent, cancellationToken).ConfigureAwait(false);
            await RenewPublicationLeaseAsync(connection, lease, cancellationToken).ConfigureAwait(false);
            var release = await registry.PublishIdempotentAsync(
                intent.OperationId,
                [intent.Change],
                intent.RequestedUtc,
                cancellationToken).ConfigureAwait(false);
            await NotifyCheckpointAsync(
                ContributionPublicationCheckpoint.RegistryCommitted,
                intent.OperationId,
                cancellationToken).ConfigureAwait(false);
            await RenewPublicationLeaseAsync(connection, lease, cancellationToken).ConfigureAwait(false);
            await CompletePublicationIntentAsync(
                connection, intent, lease, release, cancellationToken).ConfigureAwait(false);
            return release;
        }
        catch
        {
            if (publicationFaultInjector is null)
            {
                await TryReleasePublicationLeaseAsync(connection, lease).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<PublicationIntent> StorePublicationIntentAsync(
        SqliteConnection connection,
        string submissionId,
        PublicationIntentAction action,
        GlobalReleaseChange change,
        string? reviewerId,
        DateTimeOffset requestedUtc,
        CancellationToken cancellationToken)
    {
        if (action == PublicationIntentAction.Approval &&
            (string.IsNullOrWhiteSpace(reviewerId) || reviewerId.Length > 128) ||
            action == PublicationIntentAction.Withdrawal && reviewerId is not null)
        {
            throw new ContributionPolicyException("publication_intent_actor_invalid");
        }

        var actionName = action == PublicationIntentAction.Approval ? "approval" : "withdrawal";
        var operationId = PublicationOperationId(actionName, submissionId);
        var requestSha256 = GlobalReleaseCodec.Sha256(
            GlobalReleaseCodec.CreateCanonicalChangeSet([change]));
        var changeJson = JsonSerializer.Serialize(change, JsonOptions);
        if (Encoding.UTF8.GetByteCount(changeJson) > MaximumPublicationChangeJsonBytes)
        {
            throw new ContributionPolicyException("publication_intent_size_invalid");
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO publication_intents(
                operation_id, submission_id, action, request_sha256, change_json,
                reviewer_id, requested_utc, completed)
            VALUES($operation, $submission, $action, $request, $change,
                   $reviewer, $requested, 0);
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$action", (int)action);
        command.Parameters.AddWithValue("$request", requestSha256);
        command.Parameters.AddWithValue("$change", changeJson);
        command.Parameters.AddWithValue("$reviewer", (object?)reviewerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$requested", requestedUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var stored = await ReadPublicationIntentAsync(connection, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContributionPolicyException("publication_intent_missing");
        if (stored.Completed || stored.SubmissionId != submissionId || stored.Action != action ||
            stored.RequestSha256 != requestSha256 || stored.ReviewerId != reviewerId)
        {
            throw new ContributionPolicyException("publication_intent_conflict");
        }

        await ValidatePublicationIntentAsync(connection, stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private static async Task<PublicationIntent?> ReadPublicationIntentAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, submission_id, action, request_sha256, change_json,
                   reviewer_id, requested_utc, completed, release_sequence
            FROM publication_intents
            WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return ParsePublicationIntent(reader);
    }

    private static async Task<IReadOnlyList<PublicationIntent>> ReadPendingPublicationIntentsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, submission_id, action, request_sha256, change_json,
                   reviewer_id, requested_utc, completed, release_sequence
            FROM publication_intents
            WHERE completed = 0
            ORDER BY requested_utc, operation_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", MaximumRecoveryBatch);
        var intents = new List<PublicationIntent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            intents.Add(ParsePublicationIntent(reader));
        }

        return intents;
    }

    private async Task<PublicationLease?> TryClaimPublicationIntentAsync(
        SqliteConnection connection,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(PublicationLeaseDuration);
        var token = Guid.NewGuid().ToString("N");
        var priorState = await ReadPublicationLeaseStateAsync(
            connection, intent.OperationId, cancellationToken).ConfigureAwait(false);
        if (priorState?.LeaseToken is not null && priorState.ExpiresUtc > now) return null;
        if (priorState?.AttemptCount >= MaximumPublicationAttempts)
        {
            throw new ContributionPolicyException("publication_lease_attempt_limit");
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO publication_intent_leases(
                operation_id, lease_owner, lease_token, acquired_utc, expires_utc, attempt_count)
            SELECT $operation, $owner, $token, $acquired, $expires, 1
            FROM publication_intents
            WHERE operation_id = $operation AND completed = 0
            ON CONFLICT(operation_id) DO UPDATE SET
                lease_owner = excluded.lease_owner,
                lease_token = excluded.lease_token,
                acquired_utc = excluded.acquired_utc,
                expires_utc = excluded.expires_utc,
                attempt_count = publication_intent_leases.attempt_count + 1
            WHERE publication_intent_leases.attempt_count < $maximum_attempts
              AND (publication_intent_leases.lease_token IS NULL OR publication_intent_leases.expires_utc <= $acquired);
            """;
        command.Parameters.AddWithValue("$operation", intent.OperationId);
        command.Parameters.AddWithValue("$owner", workerId);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$acquired", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$maximum_attempts", MaximumPublicationAttempts);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadPublicationLeaseStateAsync(connection, intent.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (changed == 1)
        {
            if (state is null || state.LeaseOwner != workerId || state.LeaseToken != token ||
                state.AcquiredUtc != now || state.ExpiresUtc != expires)
            {
                throw new ContributionPolicyException("publication_lease_claim_conflict");
            }

            return new PublicationLease(intent.OperationId, workerId, token, expires);
        }

        var currentIntent = await ReadPublicationIntentAsync(connection, intent.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (currentIntent is null || currentIntent.Completed) return null;
        if (state is null)
        {
            throw new ContributionPolicyException("publication_lease_missing");
        }

        if (state.LeaseToken is not null && state.ExpiresUtc > now) return null;
        if (state.AttemptCount >= MaximumPublicationAttempts)
        {
            throw new ContributionPolicyException("publication_lease_attempt_limit");
        }

        throw new ContributionPolicyException("publication_lease_claim_conflict");
    }

    private static async Task<PublicationLeaseState?> ReadPublicationLeaseStateAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, lease_owner, lease_token, acquired_utc, expires_utc, attempt_count
            FROM publication_intent_leases
            WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        var storedOperationId = reader.GetString(0);
        var owner = reader.IsDBNull(1) ? null : reader.GetString(1);
        var token = reader.IsDBNull(2) ? null : reader.GetString(2);
        var acquiredText = reader.IsDBNull(3) ? null : reader.GetString(3);
        var expiresText = reader.IsDBNull(4) ? null : reader.GetString(4);
        var attempts = reader.GetInt32(5);
        var allLeaseFieldsNull = owner is null && token is null && acquiredText is null && expiresText is null;
        var allLeaseFieldsPresent = owner is not null && token is not null && acquiredText is not null && expiresText is not null;
        if (storedOperationId != operationId || attempts is < 1 or > MaximumPublicationAttempts ||
            !allLeaseFieldsNull && !allLeaseFieldsPresent)
        {
            throw new ContributionPolicyException("publication_lease_corrupt");
        }

        if (allLeaseFieldsNull)
        {
            return new PublicationLeaseState(operationId, attempts, null, null, null, null);
        }

        if (owner!.Length is < 1 or > 64 ||
            owner.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            token!.Length != 32 || !token.All(char.IsAsciiHexDigit) ||
            !DateTimeOffset.TryParseExact(
                acquiredText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var acquiredUtc) ||
            !DateTimeOffset.TryParseExact(
                expiresText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresUtc) ||
            acquiredUtc.Offset != TimeSpan.Zero || expiresUtc.Offset != TimeSpan.Zero || expiresUtc <= acquiredUtc)
        {
            throw new ContributionPolicyException("publication_lease_corrupt");
        }

        return new PublicationLeaseState(
            operationId,
            attempts,
            owner,
            token.ToLowerInvariant(),
            acquiredUtc,
            expiresUtc);
    }

    private static async Task RenewPublicationLeaseAsync(
        SqliteConnection connection,
        PublicationLease lease,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(PublicationLeaseDuration);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE publication_intent_leases
            SET expires_utc = $expires
            WHERE operation_id = $operation AND lease_owner = $owner AND lease_token = $token
              AND expires_utc > $now
              AND EXISTS(
                  SELECT 1 FROM publication_intents
                  WHERE operation_id = $operation AND completed = 0
              );
            """;
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$operation", lease.OperationId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ContributionPolicyException("publication_lease_lost");
        }
    }

    private static async Task TryReleasePublicationLeaseAsync(
        SqliteConnection connection,
        PublicationLease lease)
    {
        try
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE publication_intent_leases
                SET lease_owner = NULL, lease_token = NULL, acquired_utc = NULL, expires_utc = NULL
                WHERE operation_id = $operation AND lease_owner = $owner AND lease_token = $token;
                """;
            command.Parameters.AddWithValue("$operation", lease.OperationId);
            command.Parameters.AddWithValue("$owner", lease.Owner);
            command.Parameters.AddWithValue("$token", lease.Token);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The lease expires automatically; preserve the original publication failure.
        }
    }

    private static PublicationIntent ParsePublicationIntent(SqliteDataReader reader)
    {
        var operationId = reader.GetString(0);
        var submissionId = reader.GetString(1);
        var actionValue = reader.GetInt32(2);
        var requestSha256 = reader.GetString(3);
        var changeJson = reader.GetString(4);
        var reviewerId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var requestedText = reader.GetString(6);
        var completedValue = reader.GetInt32(7);
        long? releaseSequence = reader.IsDBNull(8) ? null : reader.GetInt64(8);
        if (!Enum.IsDefined((PublicationIntentAction)actionValue) ||
            requestSha256.Length != 64 || !requestSha256.All(char.IsAsciiHexDigit) ||
            Encoding.UTF8.GetByteCount(changeJson) > MaximumPublicationChangeJsonBytes ||
            !DateTimeOffset.TryParseExact(
                requestedText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var requestedUtc) ||
            completedValue is not 0 and not 1 || completedValue == 0 && releaseSequence is not null ||
            completedValue == 1 && releaseSequence is null)
        {
            throw new ContributionPolicyException("publication_intent_corrupt");
        }

        try
        {
            var change = JsonSerializer.Deserialize<GlobalReleaseChange>(changeJson, JsonOptions)
                ?? throw new ContributionPolicyException("publication_intent_corrupt");
            _ = GlobalReleaseCodec.CreateCanonicalChangeSet([change]);
            return new PublicationIntent(
                operationId,
                submissionId,
                (PublicationIntentAction)actionValue,
                requestSha256.ToLowerInvariant(),
                change,
                reviewerId,
                requestedUtc.ToUniversalTime(),
                completedValue == 1,
                releaseSequence);
        }
        catch (ContributionPolicyException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ContributionPolicyException("publication_intent_corrupt");
        }
        catch (GlobalReplicaException)
        {
            throw new ContributionPolicyException("publication_intent_corrupt");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new ContributionPolicyException("publication_intent_corrupt");
        }
    }

    private async Task ValidatePublicationIntentAsync(
        SqliteConnection connection,
        PublicationIntent intent,
        CancellationToken cancellationToken)
    {
        var actionName = intent.Action == PublicationIntentAction.Approval ? "approval" : "withdrawal";
        var expectedOperationId = PublicationOperationId(actionName, intent.SubmissionId);
        var expectedRequestSha256 = GlobalReleaseCodec.Sha256(
            GlobalReleaseCodec.CreateCanonicalChangeSet([intent.Change]));
        if (intent.OperationId != expectedOperationId ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(intent.RequestSha256),
                Convert.FromHexString(expectedRequestSha256)))
        {
            throw new ContributionPolicyException("publication_intent_binding_invalid");
        }

        var row = await ReadSubmissionAsync(connection, intent.SubmissionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContributionPolicyException("publication_intent_submission_missing");
        var candidate = DecodeCandidate(row);
        if (intent.Action == PublicationIntentAction.Approval)
        {
            if (row.State != ContributionState.Quarantined ||
                string.IsNullOrWhiteSpace(intent.ReviewerId) || intent.ReviewerId.Length > 128 ||
                intent.Change.Kind != GlobalReleaseChangeKind.Upsert ||
                intent.Change.Resource is not { } resource ||
                resource.ResourceId != candidate.ResourceId || resource.Version != candidate.Version ||
                resource.Kind != candidate.Kind || resource.Title != candidate.Title ||
                resource.Summary != candidate.Body || resource.SourceUri != candidate.SourceUri ||
                resource.License != candidate.License || resource.Deprecated || resource.Embedding is not null ||
                !resource.Capabilities.SequenceEqual(candidate.Capabilities, StringComparer.Ordinal) ||
                !ArtifactMatchesCandidate(resource.Artifact, candidate))
            {
                throw new ContributionPolicyException("publication_intent_candidate_mismatch");
            }

            if (candidate.Kind == KnowledgeResourceKind.Ccb)
            {
                var stagedArtifact = await ReadStagedArtifactAsync(
                    candidate.ArtifactSha256!, cancellationToken).ConfigureAwait(false);
                var verifiedReference = await registry.AddArtifactAsync(
                    stagedArtifact, "application/vnd.dcx.ccb+zip", cancellationToken).ConfigureAwait(false);
                if (resource.Artifact != verifiedReference)
                {
                    throw new ContributionPolicyException("publication_intent_artifact_mismatch");
                }
            }
        }
        else if (row.State != ContributionState.Approved || intent.ReviewerId is not null ||
                 intent.Change.Kind != GlobalReleaseChangeKind.Deprecate || intent.Change.Resource is not null ||
                 intent.Change.ResourceId != candidate.ResourceId || intent.Change.Version != candidate.Version)
        {
            throw new ContributionPolicyException("publication_intent_candidate_mismatch");
        }
    }

    private static bool ArtifactMatchesCandidate(
        GlobalArtifactReference? artifact,
        ContributionCandidate candidate) =>
        candidate.Kind switch
        {
            KnowledgeResourceKind.Ckb => artifact is null,
            KnowledgeResourceKind.Ccb => artifact is not null &&
                                         artifact.Sha256.Equals(candidate.ArtifactSha256, StringComparison.OrdinalIgnoreCase) &&
                                         artifact.SizeBytes is > 0 and <= ContentAddressedArtifactStore.MaximumArtifactBytes &&
                                         artifact.MediaType == "application/vnd.dcx.ccb+zip",
            _ => false,
        };

    private static async Task CompletePublicationIntentAsync(
        SqliteConnection connection,
        PublicationIntent intent,
        PublicationLease lease,
        GlobalReleaseDescriptor release,
        CancellationToken cancellationToken)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM publication_intent_leases
            WHERE operation_id = $operation AND lease_owner = $owner AND lease_token = $token
              AND expires_utc > $completed;
            """;
        command.Parameters.AddWithValue("$operation", intent.OperationId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$completed", completedUtc.ToString("O"));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
        {
            throw new ContributionPolicyException("publication_lease_lost");
        }

        command.Parameters.Clear();
        if (intent.Action == PublicationIntentAction.Approval)
        {
            command.CommandText = """
                UPDATE contributions
                SET state = $state, reviewer_id = $reviewer, release_sequence = $release, reviewed_utc = $reviewed
                WHERE submission_id = $submission AND state = $expected;
                """;
            command.Parameters.AddWithValue("$reviewer", intent.ReviewerId!);
            command.Parameters.AddWithValue("$state", (int)ContributionState.Approved);
            command.Parameters.AddWithValue("$expected", (int)ContributionState.Quarantined);
        }
        else
        {
            command.CommandText = """
                UPDATE contributions
                SET state = $state, release_sequence = $release, reviewed_utc = $reviewed
                WHERE submission_id = $submission AND state = $expected;
                """;
            command.Parameters.AddWithValue("$state", (int)ContributionState.Withdrawn);
            command.Parameters.AddWithValue("$expected", (int)ContributionState.Approved);
        }

        command.Parameters.AddWithValue("$release", release.Sequence);
        command.Parameters.AddWithValue("$reviewed", intent.RequestedUtc.ToString("O"));
        command.Parameters.AddWithValue("$submission", intent.SubmissionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ContributionPolicyException("publication_intent_state_conflict");
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE publication_intents
            SET completed = 1, release_sequence = $release, completed_utc = $completed
            WHERE operation_id = $operation AND completed = 0
              AND EXISTS(
                  SELECT 1 FROM publication_intent_leases
                  WHERE operation_id = $operation AND lease_owner = $owner AND lease_token = $token
                    AND expires_utc > $completed
              );
            """;
        command.Parameters.AddWithValue("$release", release.Sequence);
        command.Parameters.AddWithValue("$completed", completedUtc.ToString("O"));
        command.Parameters.AddWithValue("$operation", intent.OperationId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ContributionPolicyException("publication_intent_completion_conflict");
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE publication_intent_leases
            SET lease_owner = NULL, lease_token = NULL, acquired_utc = NULL, expires_utc = NULL
            WHERE operation_id = $operation AND lease_owner = $owner AND lease_token = $token;
            """;
        command.Parameters.AddWithValue("$operation", intent.OperationId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ContributionPolicyException("publication_lease_lost");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyCheckpointAsync(
        ContributionPublicationCheckpoint checkpoint,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (publicationFaultInjector is not null)
        {
            await publicationFaultInjector.OnCheckpointAsync(checkpoint, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ContributionScanResult MergeScans(params ContributionScanResult[] scans) =>
        new(scans
            .SelectMany(scan => scan.Findings)
            .DistinctBy(finding => finding.Code, StringComparer.Ordinal)
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ToArray());

    private static string PublicationOperationId(string action, string submissionId) =>
        $"contribution-{action}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(submissionId)))}";

    private static async Task<SubmissionRow?> ReadSubmissionAsync(
        SqliteConnection connection,
        string submissionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_sha256, candidate_json, state, contributor_pseudonym FROM contributions WHERE submission_id = $id;";
        command.Parameters.AddWithValue("$id", submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var stateValue = reader.GetInt32(2);
        if (!Enum.IsDefined((ContributionState)stateValue))
        {
            throw new ContributionPolicyException("submission_corrupt");
        }

        return new SubmissionRow(
            reader.GetString(0), reader.GetString(1), (ContributionState)stateValue, reader.GetString(3));
    }

    private static ContributionCandidate DecodeCandidate(SubmissionRow row)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<ContributionCandidate>(row.CandidateJson, JsonOptions)
                ?? throw new ContributionPolicyException("submission_corrupt");
            var actualSha256 = ContributionCodec.CandidateSha256(candidate);
            if (row.CandidateSha256.Length != 64 || !row.CandidateSha256.All(char.IsAsciiHexDigit) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(row.CandidateSha256),
                    Convert.FromHexString(actualSha256)))
            {
                throw new ContributionPolicyException("submission_corrupt");
            }

            return candidate;
        }
        catch (ContributionPolicyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw new ContributionPolicyException("submission_corrupt");
        }
    }

    private static async Task<SubmissionRow?> ReadApprovedResourceAsync(
        SqliteConnection connection,
        string resourceId,
        string version,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_sha256, candidate_json, state, contributor_pseudonym FROM contributions WHERE resource_id = $resource AND version = $version AND state = $approved LIMIT 1;";
        command.Parameters.AddWithValue("$resource", resourceId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$approved", (int)ContributionState.Approved);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubmissionRow(reader.GetString(0), reader.GetString(1), (ContributionState)reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    private static async Task<(bool Receipt, bool Nonce)> ReceiptExistsAsync(
        SqliteConnection connection,
        QualifiedUsageReceipt receipt,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT receipt_id, nonce FROM usage_receipts WHERE receipt_id = $receipt OR nonce = $nonce;";
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$nonce", receipt.Nonce);
        var receiptSeen = false;
        var nonceSeen = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            receiptSeen |= reader.GetString(0) == receipt.ReceiptId;
            nonceSeen |= reader.GetString(1) == receipt.Nonce;
        }

        return (receiptSeen, nonceSeen);
    }

    private static async Task<int> CountQualifiedForDayAsync(
        SqliteConnection connection,
        QualifiedUsageReceipt receipt,
        CancellationToken cancellationToken)
    {
        var day = receipt.OccurredUtc.UtcDateTime.Date;
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM usage_receipts
            WHERE installation_pseudonym = $installation AND resource_id = $resource AND qualified = 1
              AND occurred_utc >= $start AND occurred_utc < $end;
            """;
        command.Parameters.AddWithValue("$installation", receipt.InstallationPseudonym);
        command.Parameters.AddWithValue("$resource", receipt.ResourceId);
        command.Parameters.AddWithValue("$start", new DateTimeOffset(day, TimeSpan.Zero).ToString("O"));
        command.Parameters.AddWithValue("$end", new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed record SubmissionRow(
        string CandidateSha256,
        string CandidateJson,
        ContributionState State,
        string ContributorPseudonym);

    private enum PublicationIntentAction
    {
        Approval = 1,
        Withdrawal = 2,
    }

    private sealed record PublicationIntent(
        string OperationId,
        string SubmissionId,
        PublicationIntentAction Action,
        string RequestSha256,
        GlobalReleaseChange Change,
        string? ReviewerId,
        DateTimeOffset RequestedUtc,
        bool Completed,
        long? ReleaseSequence);

    private sealed record PublicationLease(
        string OperationId,
        string Owner,
        string Token,
        DateTimeOffset ExpiresUtc);

    private sealed record PublicationLeaseState(
        string OperationId,
        int AttemptCount,
        string? LeaseOwner,
        string? LeaseToken,
        DateTimeOffset? AcquiredUtc,
        DateTimeOffset? ExpiresUtc);
}
