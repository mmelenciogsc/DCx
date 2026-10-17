using System.Text.Json;
using System.Security.Cryptography;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
    };

    private readonly string connectionString;
    private readonly string quarantineRoot;
    private readonly string quarantineArtifactsRoot;
    private readonly GlobalRegistryRepository registry;
    private readonly ICcbSandboxExecutor? ccbSandboxExecutor;
    private readonly ContributionScanner scanner = new();
    private readonly CcbPackageValidator ccbPackageValidator = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private ContributionGovernanceService(
        string root,
        GlobalRegistryRepository registry,
        ICcbSandboxExecutor? ccbSandboxExecutor)
    {
        this.registry = registry;
        this.ccbSandboxExecutor = ccbSandboxExecutor;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(registry);
        var canonical = Path.GetFullPath(root);
        Directory.CreateDirectory(canonical);
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ContributionPolicyException("governance_root_link_denied");
        }

        var service = new ContributionGovernanceService(canonical, registry, ccbSandboxExecutor);
        await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
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

            var candidate = JsonSerializer.Deserialize<ContributionCandidate>(row.CandidateJson, JsonOptions)
                ?? throw new ContributionPolicyException("submission_corrupt");
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
            var release = await registry.PublishAsync(
                [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
                issuedUtc,
                cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE contributions
                SET state = $state, reviewer_id = $reviewer, release_sequence = $release, reviewed_utc = $reviewed
                WHERE submission_id = $submission AND state = $quarantined;
                """;
            command.Parameters.AddWithValue("$state", (int)ContributionState.Approved);
            command.Parameters.AddWithValue("$reviewer", reviewerId);
            command.Parameters.AddWithValue("$release", release.Sequence);
            command.Parameters.AddWithValue("$reviewed", issuedUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$submission", submissionId);
            command.Parameters.AddWithValue("$quarantined", (int)ContributionState.Quarantined);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("approval_state_conflict");
            }

            return release;
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

            var candidate = JsonSerializer.Deserialize<ContributionCandidate>(row.CandidateJson, JsonOptions)
                ?? throw new ContributionPolicyException("submission_corrupt");
            var release = await registry.PublishAsync(
                [new GlobalReleaseChange(GlobalReleaseChangeKind.Deprecate, candidate.ResourceId, candidate.Version)],
                issuedUtc,
                cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE contributions SET state = $state, release_sequence = $release, reviewed_utc = $reviewed WHERE submission_id = $submission;";
            command.Parameters.AddWithValue("$state", (int)ContributionState.Withdrawn);
            command.Parameters.AddWithValue("$release", release.Sequence);
            command.Parameters.AddWithValue("$reviewed", issuedUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$submission", submissionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return release;
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
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

    private static ContributionScanResult MergeScans(params ContributionScanResult[] scans) =>
        new(scans
            .SelectMany(scan => scan.Findings)
            .DistinctBy(finding => finding.Code, StringComparer.Ordinal)
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .ToArray());

    private static async Task<SubmissionRow?> ReadSubmissionAsync(
        SqliteConnection connection,
        string submissionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_sha256, candidate_json, state, contributor_pseudonym FROM contributions WHERE submission_id = $id;";
        command.Parameters.AddWithValue("$id", submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubmissionRow(reader.GetString(0), reader.GetString(1), (ContributionState)reader.GetInt32(2), reader.GetString(3))
            : null;
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
}
