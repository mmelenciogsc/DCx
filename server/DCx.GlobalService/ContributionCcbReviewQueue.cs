using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record ContributionApprovalRequestResult(
    bool Queued,
    string? ReviewJobId,
    GlobalReleaseDescriptor? Release);

public sealed record CcbReviewQueueItem(
    string JobId,
    string SubmissionId,
    DateTimeOffset RequestedUtc,
    int ConsecutiveFailures,
    string? FailureCode,
    DateTimeOffset? LastFailedUtc,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? DeadLetteredUtc,
    int RemediationCount,
    DateTimeOffset? LastRemediatedUtc);

public sealed record CcbReviewWorkerResult(
    int Completed,
    int Failed,
    int DeadLettered);

public sealed partial class ContributionGovernanceService
{
    private const int MaximumCcbReviewBatch = 10;
    private const int MaximumCcbReviewFailures = 5;
    private const int MaximumCcbReviewAttempts = 20;
    private static readonly TimeSpan CcbReviewLeaseDuration = TimeSpan.FromMinutes(30);

    public async Task<ContributionApprovalRequestResult> RequestApprovalAsync(
        string submissionId,
        string reviewerId,
        DateTimeOffset issuedUtc,
        bool ccbReviewWorkerEnabled,
        CancellationToken cancellationToken = default)
    {
        var item = await GetReviewItemAsync(submissionId, cancellationToken).ConfigureAwait(false);
        if (item.Candidate.Kind == KnowledgeResourceKind.Ccb)
        {
            if (!ccbReviewWorkerEnabled)
            {
                throw new ContributionPolicyException("ccb_review_worker_required");
            }

            var job = await EnqueueCcbReviewAsync(
                submissionId, reviewerId, issuedUtc, cancellationToken).ConfigureAwait(false);
            return new ContributionApprovalRequestResult(true, job.JobId, null);
        }

        var release = await ApproveAndPublishAsync(
            submissionId, reviewerId, issuedUtc, cancellationToken).ConfigureAwait(false);
        return new ContributionApprovalRequestResult(false, null, release);
    }

    public async Task<CcbReviewQueueItem> EnqueueCcbReviewAsync(
        string submissionId,
        string reviewerId,
        DateTimeOffset requestedUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateSubmissionId(submissionId);
        if (!IsBoundedIdentity(reviewerId))
        {
            throw new ContributionPolicyException("reviewer_invalid");
        }

        if (requestedUtc.Offset != TimeSpan.Zero)
        {
            throw new ContributionPolicyException("ccb_review_time_invalid");
        }

        if (ccbSandboxExecutor is null)
        {
            throw new ContributionPolicyException("ccb_sandbox_review_required");
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
            if (candidate.Kind != KnowledgeResourceKind.Ccb || !scanner.Scan(candidate).IsEligible)
            {
                throw new ContributionPolicyException(
                    candidate.Kind == KnowledgeResourceKind.Ccb
                        ? "quarantine_findings_present"
                        : "submission_kind_invalid");
            }

            _ = await ReadStagedArtifactAsync(
                candidate.ArtifactSha256 ?? string.Empty, cancellationToken).ConfigureAwait(false);
            var jobId = CcbReviewJobId(submissionId);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO ccb_review_jobs(
                    job_id, submission_id, candidate_sha256, reviewer_id, requested_utc,
                    completed, consecutive_failures, next_attempt_utc, remediation_count)
                VALUES($job, $submission, $candidate, $reviewer, $requested,
                       0, 0, $requested, 0);
                """;
            command.Parameters.AddWithValue("$job", jobId);
            command.Parameters.AddWithValue("$submission", submissionId);
            command.Parameters.AddWithValue("$candidate", row.CandidateSha256.ToLowerInvariant());
            command.Parameters.AddWithValue("$reviewer", reviewerId);
            command.Parameters.AddWithValue("$requested", requestedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var stored = await ReadCcbReviewJobAsync(connection, jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new ContributionPolicyException("ccb_review_job_missing");
            if (stored.Completed || stored.SubmissionId != submissionId ||
                stored.CandidateSha256 != row.CandidateSha256.ToLowerInvariant() ||
                stored.ReviewerId != reviewerId || stored.RequestedUtc != requestedUtc)
            {
                throw new ContributionPolicyException("ccb_review_job_conflict");
            }

            return stored.ToQueueItem();
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<CcbReviewWorkerResult> ProcessReadyCcbReviewJobsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (now.Offset != TimeSpan.Zero)
        {
            throw new ContributionPolicyException("ccb_review_time_invalid");
        }

        var completed = 0;
        var failed = 0;
        var deadLettered = 0;
        for (var index = 0; index < MaximumCcbReviewBatch; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await ClaimNextCcbReviewJobAsync(now, cancellationToken).ConfigureAwait(false);
            if (claimed is null) break;
            var (job, lease) = claimed.Value;
            try
            {
                var item = await GetReviewItemAsync(job.SubmissionId, cancellationToken).ConfigureAwait(false);
                if (item.State != ContributionState.Approved)
                {
                    if (item.State != ContributionState.Quarantined)
                    {
                        throw new ContributionPolicyException("ccb_review_submission_state_invalid");
                    }

                    if (ContributionCodec.CandidateSha256(item.Candidate) != job.CandidateSha256)
                    {
                        throw new ContributionPolicyException("ccb_review_candidate_mismatch");
                    }

                    await ApproveAndPublishAsync(
                        job.SubmissionId,
                        job.ReviewerId,
                        job.RequestedUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                await CompleteCcbReviewJobAsync(job, lease, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryReleaseCcbReviewLeaseAsync(lease).ConfigureAwait(false);
                throw;
            }
            catch (ContributionPolicyException exception)
            {
                if (exception.Code == "submission_not_quarantined" &&
                    (await GetReviewItemAsync(job.SubmissionId, cancellationToken).ConfigureAwait(false)).State ==
                    ContributionState.Approved)
                {
                    await CompleteCcbReviewJobAsync(job, lease, cancellationToken).ConfigureAwait(false);
                    completed++;
                    continue;
                }

                var isDead = await RecordCcbReviewFailureAsync(
                    job,
                    lease,
                    SanitizeWorkerFailureCode(exception.Code),
                    now,
                    permanent: IsPermanentCcbReviewFailure(exception.Code),
                    cancellationToken).ConfigureAwait(false);
                failed++;
                deadLettered += isDead ? 1 : 0;
            }
            catch (GlobalReplicaException)
            {
                var isDead = await RecordCcbReviewFailureAsync(
                    job,
                    lease,
                    "registry_operation_failed",
                    now,
                    permanent: false,
                    cancellationToken).ConfigureAwait(false);
                failed++;
                deadLettered += isDead ? 1 : 0;
            }
            catch (SqliteException)
            {
                var isDead = await RecordCcbReviewFailureAsync(
                    job,
                    lease,
                    "ccb_review_database_failed",
                    now,
                    permanent: false,
                    cancellationToken).ConfigureAwait(false);
                failed++;
                deadLettered += isDead ? 1 : 0;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                var isDead = await RecordCcbReviewFailureAsync(
                    job,
                    lease,
                    exception is UnauthorizedAccessException
                        ? "ccb_review_storage_access_denied"
                        : exception is CryptographicException
                            ? "ccb_review_cryptography_failed"
                            : "ccb_review_storage_failed",
                    now,
                    permanent: exception is UnauthorizedAccessException or CryptographicException,
                    cancellationToken).ConfigureAwait(false);
                failed++;
                deadLettered += isDead ? 1 : 0;
            }
        }

        return new CcbReviewWorkerResult(completed, failed, deadLettered);
    }

    public async Task<IReadOnlyList<CcbReviewQueueItem>> GetCcbReviewQueueAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ContributionPolicyException("ccb_review_queue_limit_invalid");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id, submission_id, candidate_sha256, reviewer_id, requested_utc,
                       completed, completed_utc, consecutive_failures, failure_code,
                       last_failed_utc, next_attempt_utc, dead_lettered_utc,
                       remediation_count, last_remediated_utc
                FROM ccb_review_jobs
                WHERE completed = 0
                ORDER BY (dead_lettered_utc IS NULL),
                         COALESCE(dead_lettered_utc, next_attempt_utc), job_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var items = new List<CcbReviewQueueItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ParseCcbReviewJob(reader).ToQueueItem());
            }

            return items;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<CcbReviewQueueItem> RetryCcbReviewDeadLetterAsync(
        string jobId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateCcbReviewJobId(jobId);
        if (now.Offset != TimeSpan.Zero)
        {
            throw new ContributionPolicyException("ccb_review_time_invalid");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE ccb_review_jobs
                SET consecutive_failures = 0,
                    failure_code = NULL,
                    last_failed_utc = NULL,
                    next_attempt_utc = $next,
                    dead_lettered_utc = NULL,
                    remediation_count = remediation_count + 1,
                    last_remediated_utc = $next
                WHERE job_id = $job AND completed = 0 AND dead_lettered_utc IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$next", now.ToString("O"));
            command.Parameters.AddWithValue("$job", jobId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("ccb_review_dead_letter_missing");
            }

            command.Parameters.Clear();
            command.CommandText = "DELETE FROM ccb_review_job_leases WHERE job_id = $job;";
            command.Parameters.AddWithValue("$job", jobId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadCcbReviewJobAsync(connection, jobId, cancellationToken).ConfigureAwait(false)
                       ?? throw new ContributionPolicyException("ccb_review_job_missing"))
                .ToQueueItem();
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private static async Task InitializeCcbReviewQueueAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ccb_review_jobs(
                job_id TEXT PRIMARY KEY,
                submission_id TEXT NOT NULL UNIQUE,
                candidate_sha256 TEXT NOT NULL,
                reviewer_id TEXT NOT NULL,
                requested_utc TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0,
                completed_utc TEXT,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                failure_code TEXT,
                last_failed_utc TEXT,
                next_attempt_utc TEXT,
                dead_lettered_utc TEXT,
                remediation_count INTEGER NOT NULL DEFAULT 0,
                last_remediated_utc TEXT,
                FOREIGN KEY(submission_id) REFERENCES contributions(submission_id),
                CHECK(completed IN (0, 1)),
                CHECK(consecutive_failures >= 0 AND consecutive_failures <= 5),
                CHECK(remediation_count >= 0),
                CHECK(failure_code IS NULL OR (length(failure_code) >= 1 AND length(failure_code) <= 64)),
                CHECK(
                    (completed = 1 AND completed_utc IS NOT NULL AND next_attempt_utc IS NULL AND dead_lettered_utc IS NULL) OR
                    (completed = 0 AND completed_utc IS NULL AND dead_lettered_utc IS NULL AND next_attempt_utc IS NOT NULL) OR
                    (completed = 0 AND completed_utc IS NULL AND dead_lettered_utc IS NOT NULL AND next_attempt_utc IS NULL)
                ),
                CHECK(
                    (consecutive_failures = 0 AND failure_code IS NULL AND last_failed_utc IS NULL) OR
                    (consecutive_failures > 0 AND failure_code IS NOT NULL AND last_failed_utc IS NOT NULL)
                )
            );
            CREATE TABLE IF NOT EXISTS ccb_review_job_leases(
                job_id TEXT PRIMARY KEY,
                lease_owner TEXT,
                lease_token TEXT UNIQUE,
                acquired_utc TEXT,
                expires_utc TEXT,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(job_id) REFERENCES ccb_review_jobs(job_id) ON DELETE CASCADE,
                CHECK(attempt_count >= 0 AND attempt_count <= 20),
                CHECK(
                    (lease_owner IS NULL AND lease_token IS NULL AND acquired_utc IS NULL AND expires_utc IS NULL) OR
                    (lease_owner IS NOT NULL AND lease_token IS NOT NULL AND acquired_utc IS NOT NULL AND expires_utc IS NOT NULL)
                )
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(CcbReviewJob Job, CcbReviewLease Lease)?> ClaimNextCcbReviewJobAsync(
        DateTimeOffset scheduleUtc,
        CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT j.job_id, j.submission_id, j.candidate_sha256, j.reviewer_id, j.requested_utc,
                       j.completed, j.completed_utc, j.consecutive_failures, j.failure_code,
                       j.last_failed_utc, j.next_attempt_utc, j.dead_lettered_utc,
                       j.remediation_count, j.last_remediated_utc
                FROM ccb_review_jobs AS j
                WHERE j.completed = 0 AND j.dead_lettered_utc IS NULL
                  AND j.next_attempt_utc <= $schedule
                ORDER BY j.requested_utc, j.job_id
                LIMIT $limit;
                """;
            var acquiredUtc = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("$schedule", scheduleUtc.ToString("O"));
            command.Parameters.AddWithValue("$limit", MaximumCcbReviewBatch);
            var candidates = new List<CcbReviewJob>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add(ParseCcbReviewJob(reader));
                }
            }

            CcbReviewJob? job = null;
            foreach (var candidate in candidates)
            {
                var leaseState = await ReadCcbReviewLeaseStateAsync(
                    connection, candidate.JobId, cancellationToken).ConfigureAwait(false);
                if (leaseState?.LeaseToken is not null && leaseState.ExpiresUtc > acquiredUtc)
                {
                    continue;
                }

                if (leaseState?.AttemptCount >= MaximumCcbReviewAttempts)
                {
                    await MarkCcbReviewAttemptLimitAsync(
                        connection, candidate.JobId, acquiredUtc, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                job = candidate;
                break;
            }

            if (job is null) return null;
            var token = Guid.NewGuid().ToString("N");
            var expiresUtc = acquiredUtc.Add(CcbReviewLeaseDuration);
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO ccb_review_job_leases(
                    job_id, lease_owner, lease_token, acquired_utc, expires_utc, attempt_count)
                VALUES($job, $owner, $token, $acquired, $expires, 1)
                ON CONFLICT(job_id) DO UPDATE SET
                    lease_owner = excluded.lease_owner,
                    lease_token = excluded.lease_token,
                    acquired_utc = excluded.acquired_utc,
                    expires_utc = excluded.expires_utc,
                    attempt_count = ccb_review_job_leases.attempt_count + 1
                WHERE ccb_review_job_leases.attempt_count < $maximum
                  AND (ccb_review_job_leases.lease_token IS NULL OR ccb_review_job_leases.expires_utc <= $acquired);
                """;
            command.Parameters.AddWithValue("$job", job.JobId);
            command.Parameters.AddWithValue("$owner", workerId);
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$acquired", acquiredUtc.ToString("O"));
            command.Parameters.AddWithValue("$expires", expiresUtc.ToString("O"));
            command.Parameters.AddWithValue("$maximum", MaximumCcbReviewAttempts);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await MarkCcbReviewAttemptLimitAsync(
                    connection, job.JobId, acquiredUtc, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return (job, new CcbReviewLease(job.JobId, workerId, token));
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private static async Task<CcbReviewLeaseState?> ReadCcbReviewLeaseStateAsync(
        SqliteConnection connection,
        string jobId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, lease_owner, lease_token, acquired_utc, expires_utc, attempt_count
            FROM ccb_review_job_leases
            WHERE job_id = $job;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var storedJobId = reader.GetString(0);
        var owner = reader.IsDBNull(1) ? null : reader.GetString(1);
        var token = reader.IsDBNull(2) ? null : reader.GetString(2);
        var acquiredText = reader.IsDBNull(3) ? null : reader.GetString(3);
        var expiresText = reader.IsDBNull(4) ? null : reader.GetString(4);
        var attempts = reader.GetInt32(5);
        var allNull = owner is null && token is null && acquiredText is null && expiresText is null;
        var allPresent = owner is not null && token is not null && acquiredText is not null && expiresText is not null;
        if (storedJobId != jobId || attempts is < 1 or > MaximumCcbReviewAttempts ||
            !allNull && !allPresent)
        {
            throw new ContributionPolicyException("ccb_review_lease_corrupt");
        }

        if (allNull)
        {
            return new CcbReviewLeaseState(attempts, null, null, null, null);
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
            acquiredUtc.Offset != TimeSpan.Zero || expiresUtc.Offset != TimeSpan.Zero ||
            expiresUtc <= acquiredUtc)
        {
            throw new ContributionPolicyException("ccb_review_lease_corrupt");
        }

        return new CcbReviewLeaseState(
            attempts,
            owner,
            token.ToLowerInvariant(),
            acquiredUtc,
            expiresUtc);
    }

    private static async Task MarkCcbReviewAttemptLimitAsync(
        SqliteConnection connection,
        string jobId,
        DateTimeOffset failedUtc,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ccb_review_jobs
            SET consecutive_failures = $failures,
                failure_code = 'ccb_review_attempt_limit',
                last_failed_utc = $failed,
                next_attempt_utc = NULL,
                dead_lettered_utc = $failed
            WHERE job_id = $job AND completed = 0
              AND EXISTS(
                  SELECT 1 FROM ccb_review_job_leases
                  WHERE job_id = $job AND attempt_count >= $maximum
                    AND (lease_token IS NULL OR expires_utc <= $failed)
              );
            """;
        command.Parameters.AddWithValue("$failures", MaximumCcbReviewFailures);
        command.Parameters.AddWithValue("$failed", failedUtc.ToString("O"));
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$maximum", MaximumCcbReviewAttempts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteCcbReviewJobAsync(
        CcbReviewJob job,
        CcbReviewLease lease,
        CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var completedUtc = DateTimeOffset.UtcNow;
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE ccb_review_jobs
                SET completed = 1, completed_utc = $completed,
                    next_attempt_utc = NULL, dead_lettered_utc = NULL
                WHERE job_id = $job AND completed = 0
                  AND EXISTS(
                      SELECT 1 FROM contributions
                      WHERE submission_id = $submission AND state = $approved
                  )
                  AND EXISTS(
                      SELECT 1 FROM ccb_review_job_leases
                      WHERE job_id = $job AND lease_owner = $owner AND lease_token = $token
                        AND expires_utc > $completed
                  );
                """;
            command.Parameters.AddWithValue("$completed", completedUtc.ToString("O"));
            command.Parameters.AddWithValue("$job", job.JobId);
            command.Parameters.AddWithValue("$submission", job.SubmissionId);
            command.Parameters.AddWithValue("$approved", (int)ContributionState.Approved);
            command.Parameters.AddWithValue("$owner", lease.Owner);
            command.Parameters.AddWithValue("$token", lease.Token);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("ccb_review_lease_lost");
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE ccb_review_job_leases
                SET lease_owner = NULL, lease_token = NULL, acquired_utc = NULL, expires_utc = NULL
                WHERE job_id = $job AND lease_owner = $owner AND lease_token = $token;
                """;
            command.Parameters.AddWithValue("$job", job.JobId);
            command.Parameters.AddWithValue("$owner", lease.Owner);
            command.Parameters.AddWithValue("$token", lease.Token);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("ccb_review_lease_lost");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task<bool> RecordCcbReviewFailureAsync(
        CcbReviewJob job,
        CcbReviewLease lease,
        string code,
        DateTimeOffset failedUtc,
        bool permanent,
        CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT consecutive_failures FROM ccb_review_jobs WHERE job_id = $job AND completed = 0;";
            command.Parameters.AddWithValue("$job", job.JobId);
            var currentValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (currentValue is null or DBNull)
            {
                throw new ContributionPolicyException("ccb_review_job_missing");
            }

            var failures = Convert.ToInt32(currentValue) + 1;
            if (failures is < 1 or > MaximumCcbReviewFailures)
            {
                throw new ContributionPolicyException("ccb_review_queue_corrupt");
            }

            var isDead = permanent || failures >= MaximumCcbReviewFailures;
            var next = isDead
                ? (DateTimeOffset?)null
                : failedUtc.AddSeconds(Math.Min(Math.Pow(2, failures), 300));
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE ccb_review_jobs
                SET consecutive_failures = $failures,
                    failure_code = $code,
                    last_failed_utc = $failed,
                    next_attempt_utc = $next,
                    dead_lettered_utc = $dead
                WHERE job_id = $job AND completed = 0
                  AND EXISTS(
                      SELECT 1 FROM ccb_review_job_leases
                      WHERE job_id = $job AND lease_owner = $owner AND lease_token = $token
                  );
                """;
            command.Parameters.AddWithValue("$failures", failures);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$failed", failedUtc.ToString("O"));
            command.Parameters.AddWithValue("$next", next is null ? DBNull.Value : next.Value.ToString("O"));
            command.Parameters.AddWithValue("$dead", isDead ? failedUtc.ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$job", job.JobId);
            command.Parameters.AddWithValue("$owner", lease.Owner);
            command.Parameters.AddWithValue("$token", lease.Token);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("ccb_review_lease_lost");
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE ccb_review_job_leases
                SET lease_owner = NULL, lease_token = NULL, acquired_utc = NULL, expires_utc = NULL
                WHERE job_id = $job AND lease_owner = $owner AND lease_token = $token;
                """;
            command.Parameters.AddWithValue("$job", job.JobId);
            command.Parameters.AddWithValue("$owner", lease.Owner);
            command.Parameters.AddWithValue("$token", lease.Token);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ContributionPolicyException("ccb_review_lease_lost");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return isDead;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task TryReleaseCcbReviewLeaseAsync(CcbReviewLease lease)
    {
        try
        {
            await mutationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE ccb_review_job_leases
                    SET lease_owner = NULL, lease_token = NULL, acquired_utc = NULL, expires_utc = NULL
                    WHERE job_id = $job AND lease_owner = $owner AND lease_token = $token;
                    """;
                command.Parameters.AddWithValue("$job", lease.JobId);
                command.Parameters.AddWithValue("$owner", lease.Owner);
                command.Parameters.AddWithValue("$token", lease.Token);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                mutationLock.Release();
            }
        }
        catch
        {
            // The bounded lease expires; preserve the original cancellation.
        }
    }

    private static CcbReviewJob ParseCcbReviewJob(SqliteDataReader reader)
    {
        var jobId = reader.GetString(0);
        var submissionId = reader.GetString(1);
        var candidateSha256 = reader.GetString(2);
        var reviewerId = reader.GetString(3);
        var requestedUtc = ParseCcbReviewTimestamp(reader.GetString(4));
        var completedValue = reader.GetInt32(5);
        DateTimeOffset? completedUtc = reader.IsDBNull(6)
            ? null
            : ParseCcbReviewTimestamp(reader.GetString(6));
        var failures = reader.GetInt32(7);
        var failureCode = reader.IsDBNull(8) ? null : reader.GetString(8);
        DateTimeOffset? lastFailedUtc = reader.IsDBNull(9)
            ? null
            : ParseCcbReviewTimestamp(reader.GetString(9));
        DateTimeOffset? nextAttemptUtc = reader.IsDBNull(10)
            ? null
            : ParseCcbReviewTimestamp(reader.GetString(10));
        DateTimeOffset? deadLetteredUtc = reader.IsDBNull(11)
            ? null
            : ParseCcbReviewTimestamp(reader.GetString(11));
        var remediationCount = reader.GetInt32(12);
        DateTimeOffset? lastRemediatedUtc = reader.IsDBNull(13)
            ? null
            : ParseCcbReviewTimestamp(reader.GetString(13));

        ValidateCcbReviewJobId(jobId);
        ValidateSubmissionId(submissionId);
        if (jobId != CcbReviewJobId(submissionId) ||
            candidateSha256.Length != 64 || !candidateSha256.All(char.IsAsciiHexDigit) ||
            !IsBoundedIdentity(reviewerId) || completedValue is not 0 and not 1 ||
            failures is < 0 or > MaximumCcbReviewFailures || remediationCount < 0 ||
            (failureCode is null) != (failures == 0) ||
            (lastFailedUtc is null) != (failures == 0) ||
            failureCode is not null && failureCode != SanitizeWorkerFailureCode(failureCode) ||
            (remediationCount == 0) != (lastRemediatedUtc is null) ||
            completedValue == 1 && (completedUtc is null || nextAttemptUtc is not null || deadLetteredUtc is not null) ||
            completedValue == 0 && completedUtc is not null ||
            completedValue == 0 && (nextAttemptUtc is null == (deadLetteredUtc is null)))
        {
            throw new ContributionPolicyException("ccb_review_queue_corrupt");
        }

        return new CcbReviewJob(
            jobId,
            submissionId,
            candidateSha256.ToLowerInvariant(),
            reviewerId,
            requestedUtc,
            completedValue == 1,
            completedUtc,
            failures,
            failureCode,
            lastFailedUtc,
            nextAttemptUtc,
            deadLetteredUtc,
            remediationCount,
            lastRemediatedUtc);
    }

    private static async Task<CcbReviewJob?> ReadCcbReviewJobAsync(
        SqliteConnection connection,
        string jobId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, submission_id, candidate_sha256, reviewer_id, requested_utc,
                   completed, completed_utc, consecutive_failures, failure_code,
                   last_failed_utc, next_attempt_utc, dead_lettered_utc,
                   remediation_count, last_remediated_utc
            FROM ccb_review_jobs
            WHERE job_id = $job;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ParseCcbReviewJob(reader)
            : null;
    }

    private static string CcbReviewJobId(string submissionId) =>
        $"ccb-review:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(submissionId)))}";

    private static void ValidateCcbReviewJobId(string jobId)
    {
        if (!jobId.StartsWith("ccb-review:", StringComparison.Ordinal) || jobId.Length != 75 ||
            !jobId.AsSpan(11).ToArray().All(char.IsAsciiHexDigit))
        {
            throw new ContributionPolicyException("ccb_review_job_id_invalid");
        }
    }

    private static DateTimeOffset ParseCcbReviewTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        {
            throw new ContributionPolicyException("ccb_review_queue_corrupt");
        }

        return timestamp;
    }

    private static bool IsPermanentCcbReviewFailure(string code) => code switch
    {
        "publication_intent_busy" or
        "publication_lease_lost" or
        "publication_lease_claim_conflict" or
        "ccb_review_lease_lost" => false,
        _ => true,
    };

    private sealed record CcbReviewLease(string JobId, string Owner, string Token);

    private sealed record CcbReviewLeaseState(
        int AttemptCount,
        string? LeaseOwner,
        string? LeaseToken,
        DateTimeOffset? AcquiredUtc,
        DateTimeOffset? ExpiresUtc);

    private sealed record CcbReviewJob(
        string JobId,
        string SubmissionId,
        string CandidateSha256,
        string ReviewerId,
        DateTimeOffset RequestedUtc,
        bool Completed,
        DateTimeOffset? CompletedUtc,
        int ConsecutiveFailures,
        string? FailureCode,
        DateTimeOffset? LastFailedUtc,
        DateTimeOffset? NextAttemptUtc,
        DateTimeOffset? DeadLetteredUtc,
        int RemediationCount,
        DateTimeOffset? LastRemediatedUtc)
    {
        public CcbReviewQueueItem ToQueueItem() => new(
            JobId,
            SubmissionId,
            RequestedUtc,
            ConsecutiveFailures,
            FailureCode,
            LastFailedUtc,
            NextAttemptUtc,
            DeadLetteredUtc,
            RemediationCount,
            LastRemediatedUtc);
    }
}
