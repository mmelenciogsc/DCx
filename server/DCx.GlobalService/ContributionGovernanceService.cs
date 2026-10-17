using System.Text.Json;
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
    private readonly GlobalRegistryRepository registry;
    private readonly ContributionScanner scanner = new();
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private ContributionGovernanceService(string root, GlobalRegistryRepository registry)
    {
        this.registry = registry;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(registry);
        Directory.CreateDirectory(Path.GetFullPath(root));
        var service = new ContributionGovernanceService(Path.GetFullPath(root), registry);
        await service.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return service;
    }

    public async Task<ContributionState> IntakeAsync(
        PreparedContribution submission,
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

            if (candidate.Kind != KnowledgeResourceKind.Ckb)
            {
                throw new ContributionPolicyException("ccb_sandbox_review_required");
            }

            var resource = new GlobalResourceRecord(
                candidate.ResourceId,
                candidate.Version,
                candidate.Kind,
                candidate.Title,
                candidate.Body,
                candidate.SourceUri,
                candidate.License,
                candidate.Capabilities);
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
