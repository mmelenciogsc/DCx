using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record CareBlockedCaseDispositionAuditRecord(
    long Sequence,
    string CaseId,
    DateTimeOffset OccurredUtc,
    string OperatorPseudonymSha256,
    string DispositionCode,
    string ReviewedSha256,
    string PreviousRecordSha256,
    string RecordSha256);

public sealed partial class DurableCareRepository
{
    private const string BlockedCaseAuditGenesis =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public async Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await RequireNoMissingBlockedCaseReferencesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var result = await ReadBlockedCaseReviewsAsync(
                connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await ValidateBlockedCaseDispositionAuditAsync(
                connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        CareBlockedCaseReviewPolicy.RequireList(result);
        return result;
    }

    public async Task<CareBlockedCaseDispositionResult> RecordBlockedCaseDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        string operatorPseudonymSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        CareBlockedCaseReviewPolicy.RequireRequest(request);
        var operatorDigest = CareOperatorActionAuditIntegrity
            .NormalizeOperatorPseudonymSha256(operatorPseudonymSha256);
        if (!request.ExplicitlyApproved)
        {
            throw new InvalidOperationException(
                "Explicit approval is required to record a CARE blocked-case disposition.");
        }

        var nowUtc = now.ToUniversalTime();
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await RequireNoMissingBlockedCaseReferencesAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var reviews = await ReadBlockedCaseReviewsAsync(
                    connection, transaction, cancellationToken, request.CaseId)
                .ConfigureAwait(false);
            var review = reviews.SingleOrDefault() ?? throw new InvalidOperationException(
                "The CARE blocked case is unavailable or no longer blocked.");
            if (!string.Equals(review.ReviewSha256, request.ReviewedSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The reviewed CARE blocked case changed before disposition.");
            }

            var audit = await ValidateBlockedCaseDispositionAuditAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var priorForCase = audit.LastOrDefault(item =>
                string.Equals(item.CaseId, request.CaseId, StringComparison.Ordinal));
            if (priorForCase is not null &&
                string.Equals(priorForCase.DispositionCode, request.DispositionCode, StringComparison.Ordinal) &&
                string.Equals(priorForCase.ReviewedSha256, request.ReviewedSha256, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var replayResult = new CareBlockedCaseDispositionResult(
                    priorForCase.CaseId,
                    priorForCase.DispositionCode,
                    priorForCase.OccurredUtc,
                    ProviderCalls: 0,
                    FinancialMutations: 0);
                CareBlockedCaseReviewPolicy.RequireResult(replayResult);
                return replayResult;
            }

            var sequence = audit.Count == 0 ? 1 : checked(audit[^1].Sequence + 1);
            var previousSha256 = audit.Count == 0
                ? BlockedCaseAuditGenesis
                : audit[^1].RecordSha256;
            var recordSha256 = BlockedCaseDispositionAuditSha256(
                sequence,
                request.CaseId,
                nowUtc,
                operatorDigest,
                request.DispositionCode,
                request.ReviewedSha256,
                previousSha256);
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO care_blocked_case_dispositions(
                    sequence, case_id, occurred_utc, operator_pseudonym_sha256,
                    disposition_code, reviewed_sha256, previous_record_sha256,
                    record_sha256)
                VALUES($sequence, $case, $occurred, $operator, $disposition,
                    $reviewed, $previous, $record);
                """;
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$case", request.CaseId);
            command.Parameters.AddWithValue("$occurred", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("$operator", operatorDigest);
            command.Parameters.AddWithValue("$disposition", request.DispositionCode);
            command.Parameters.AddWithValue("$reviewed", request.ReviewedSha256);
            command.Parameters.AddWithValue("$previous", previousSha256);
            command.Parameters.AddWithValue("$record", recordSha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var result = new CareBlockedCaseDispositionResult(
                request.CaseId,
                request.DispositionCode,
                nowUtc,
                ProviderCalls: 0,
                FinancialMutations: 0);
            CareBlockedCaseReviewPolicy.RequireResult(result);
            return result;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<IReadOnlyList<CareBlockedCaseDispositionAuditRecord>>
        GetBlockedCaseDispositionAuditAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ValidateBlockedCaseDispositionAuditAsync(
                connection, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateBlockedCaseReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT q.event_id, q.event_type, q.reason_code, q.attempt_count,
                   q.first_received_utc, q.last_attempt_utc, q.verified_event_sha256
            FROM webhook_event_quarantine AS q
            LEFT JOIN care_blocked_cases AS c ON c.event_id = q.event_id
            WHERE q.event_id = $event AND q.state = 'blocked' AND c.event_id IS NULL;
            """;
        command.Parameters.AddWithValue("$event", eventId);
        BlockedCaseSource? source = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                source = new BlockedCaseSource(
                    reader.GetString(0),
                    EventKind(reader.GetString(1)),
                    PublicReason(reader.GetString(2)),
                    checked((int)reader.GetInt64(3)),
                    DateTimeOffset.Parse(reader.GetString(4)).ToUniversalTime(),
                    reader.IsDBNull(5)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(5)).ToUniversalTime(),
                    reader.GetString(6));
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "A blocked CARE event resolved to multiple case candidates.");
                }
            }
        }

        if (source is not null)
        {
            if (source.LastAttemptUtc is null)
            {
                throw new InvalidOperationException(
                    "A blocked CARE case has no bounded last-attempt evidence.");
            }

            var caseId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var reviewSha256 = BlockedCaseReviewSha256(caseId, source);
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO care_blocked_cases(
                    event_id, case_id, review_sha256, created_utc)
                VALUES($event, $case, $review, $created);
                """;
            command.Parameters.AddWithValue("$event", source.EventId);
            command.Parameters.AddWithValue("$case", caseId);
            command.Parameters.AddWithValue("$review", reviewSha256);
            command.Parameters.AddWithValue("$created", nowUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task BackfillBlockedCaseReferencesAsync(
        SqliteConnection connection,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var migrated = 0;
        while (true)
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT q.event_id
                FROM webhook_event_quarantine AS q
                LEFT JOIN care_blocked_cases AS c ON c.event_id = q.event_id
                WHERE q.state = 'blocked' AND c.event_id IS NULL
                ORDER BY q.first_received_utc, q.event_id
                LIMIT 100;
                """;
            var eventIds = new List<string>(CareBlockedCaseReviewPolicy.MaximumCases);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    eventIds.Add(reader.GetString(0));
                }
            }

            if (eventIds.Count == 0) return;
            migrated = checked(migrated + eventIds.Count);
            if (migrated > CareOperationsPolicy.MaximumAggregateCount)
            {
                throw new InvalidOperationException(
                    "The CARE blocked-case migration exceeds its safety bound.");
            }

            foreach (var eventId in eventIds)
            {
                await CreateBlockedCaseReferenceAsync(
                        connection, null, eventId, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task RequireNoMissingBlockedCaseReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM webhook_event_quarantine AS q
            LEFT JOIN care_blocked_cases AS c ON c.event_id = q.event_id
            WHERE q.state = 'blocked' AND c.event_id IS NULL;
            """;
        var missing = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (missing != 0)
        {
            throw new InvalidOperationException(
                "A blocked CARE event has no immutable case reference.");
        }
    }

    private static async Task<IReadOnlyList<CareBlockedCaseReview>> ReadBlockedCaseReviewsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken,
        string? caseId = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.case_id, c.review_sha256, q.event_id, q.event_type,
                   q.reason_code, q.attempt_count, q.first_received_utc,
                   q.last_attempt_utc, q.verified_event_sha256,
                   COALESCE((
                       SELECT d.disposition_code
                       FROM care_blocked_case_dispositions AS d
                       WHERE d.case_id = c.case_id
                       ORDER BY d.sequence DESC
                       LIMIT 1), 'unreviewed')
            FROM care_blocked_cases AS c
            INNER JOIN webhook_event_quarantine AS q ON q.event_id = c.event_id
            WHERE q.state = 'blocked' AND ($case IS NULL OR c.case_id = $case)
            ORDER BY q.first_received_utc, c.case_id
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$case", (object?)caseId ?? DBNull.Value);
        var result = new List<CareBlockedCaseReview>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = new BlockedCaseSource(
                reader.GetString(2),
                EventKind(reader.GetString(3)),
                PublicReason(reader.GetString(4)),
                checked((int)reader.GetInt64(5)),
                DateTimeOffset.Parse(reader.GetString(6)).ToUniversalTime(),
                reader.IsDBNull(7)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(7)).ToUniversalTime(),
                reader.GetString(8));
            var currentSha256 = BlockedCaseReviewSha256(reader.GetString(0), source);
            if (!string.Equals(currentSha256, reader.GetString(1), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The CARE blocked-case review binding failed integrity validation.");
            }

            result.Add(new CareBlockedCaseReview(
                reader.GetString(0),
                source.EventKind,
                source.ReasonCode,
                source.AttemptCount,
                source.FirstReceivedUtc,
                source.LastAttemptUtc,
                currentSha256,
                reader.GetString(9)));
        }

        CareBlockedCaseReviewPolicy.RequireList(result);
        return result;
    }

    private static async Task<IReadOnlyList<CareBlockedCaseDispositionAuditRecord>>
        ValidateBlockedCaseDispositionAuditAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, case_id, occurred_utc, operator_pseudonym_sha256,
                   disposition_code, reviewed_sha256, previous_record_sha256,
                   record_sha256
            FROM care_blocked_case_dispositions
            ORDER BY sequence;
            """;
        var result = new List<CareBlockedCaseDispositionAuditRecord>();
        var expectedSequence = 1L;
        var previous = BlockedCaseAuditGenesis;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = new CareBlockedCaseDispositionAuditRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)).ToUniversalTime(),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7));
            var expectedSha256 = BlockedCaseDispositionAuditSha256(
                item.Sequence,
                item.CaseId,
                item.OccurredUtc,
                item.OperatorPseudonymSha256,
                item.DispositionCode,
                item.ReviewedSha256,
                item.PreviousRecordSha256);
            if (item.Sequence != expectedSequence ||
                !string.Equals(item.PreviousRecordSha256, previous, StringComparison.Ordinal) ||
                !string.Equals(item.RecordSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The CARE blocked-case disposition audit failed integrity validation.");
            }

            result.Add(item);
            expectedSequence++;
            previous = item.RecordSha256;
        }

        return result;
    }

    private static string BlockedCaseReviewSha256(string caseId, BlockedCaseSource source) =>
        Sha256(new
        {
            caseId,
            source.EventId,
            source.EventKind,
            source.ReasonCode,
            source.AttemptCount,
            FirstReceivedUtc = source.FirstReceivedUtc.ToString("O"),
            LastAttemptUtc = source.LastAttemptUtc?.ToString("O"),
            source.VerifiedEventSha256,
        });

    private static string BlockedCaseDispositionAuditSha256(
        long sequence,
        string caseId,
        DateTimeOffset occurredUtc,
        string operatorPseudonymSha256,
        string dispositionCode,
        string reviewedSha256,
        string previousRecordSha256) =>
        Sha256(new
        {
            sequence,
            caseId,
            OccurredUtc = occurredUtc.ToUniversalTime().ToString("O"),
            operatorPseudonymSha256,
            dispositionCode,
            reviewedSha256,
            previousRecordSha256,
        });

    private static string Sha256<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string EventKind(string eventType) => eventType switch
    {
        "PAYMENT.SALE.COMPLETED" => "sale",
        "PAYMENT.SALE.REFUNDED" => "refund",
        "PAYMENT.SALE.REVERSED" => "reversal",
        _ => throw new InvalidOperationException("A blocked CARE case has an invalid event kind."),
    };

    private static string PublicReason(string reasonCode) => reasonCode switch
    {
        "paypal_sandbox_quarantine_identity_conflict" => "identity_conflict",
        "paypal_sandbox_quarantine_reprocess_blocked" => "policy_rejected",
        _ => throw new InvalidOperationException("A blocked CARE case has an invalid reason."),
    };

    private sealed record BlockedCaseSource(
        string EventId,
        string EventKind,
        string ReasonCode,
        int AttemptCount,
        DateTimeOffset FirstReceivedUtc,
        DateTimeOffset? LastAttemptUtc,
        string VerifiedEventSha256);
}
