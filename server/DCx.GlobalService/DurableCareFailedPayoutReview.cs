using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

internal sealed record DurableCareFailedPayoutDispositionAuditRecord(
    long Sequence,
    string ReviewId,
    DateTimeOffset OccurredUtc,
    string OperatorPseudonymSha256,
    string DispositionCode,
    string ReviewedSha256,
    string PreviousRecordSha256,
    string RecordSha256);

public sealed class DurableCareFailedPayoutReviewSource(
    DurableCareRepository repository,
    string operatorPseudonymSha256,
    Func<DateTimeOffset>? utcNow = null) : ICareFailedPayoutReviewSource
{
    private readonly DurableCareRepository repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly string operatorPseudonymSha256 = CareOperatorActionAuditIntegrity
        .NormalizeOperatorPseudonymSha256(operatorPseudonymSha256);
    private readonly Func<DateTimeOffset> utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default) =>
        repository.ListFailedPayoutReviewsAsync(cancellationToken);

    public Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default) =>
        repository.RecordFailedPayoutDispositionAsync(
            request,
            operatorPseudonymSha256,
            utcNow(),
            cancellationToken);
}

public sealed partial class DurableCareRepository
{
    private const string FailedPayoutAuditGenesis =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public async Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await RequireNoMissingFailedPayoutHoldReferencesAsync(
                connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var audit = await ReadAndVerifyFailedPayoutDispositionAuditAsync(
                connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var records = await ReadFailedPayoutReviewRecordsAsync(
                connection, transaction, audit, null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var result = records.Select(item => item.Review).ToArray();
        CareFailedPayoutReviewPolicy.RequireList(result);
        return result;
    }

    public async Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        string operatorPseudonymSha256,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        CareFailedPayoutReviewPolicy.RequireRequest(request);
        var operatorDigest = CareOperatorActionAuditIntegrity
            .NormalizeOperatorPseudonymSha256(operatorPseudonymSha256);
        var nowUtc = now.ToUniversalTime();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A failed-payout disposition requires an explicit UTC time.");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await RequireNoMissingFailedPayoutHoldReferencesAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var audit = await ReadAndVerifyFailedPayoutDispositionAuditAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var existing = audit.SingleOrDefault(item =>
                string.Equals(item.ReviewId, request.ReviewId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.DispositionCode,
                        request.DispositionCode,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        existing.ReviewedSha256,
                        request.ReviewedSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The failed-payout hold already has a conflicting decision.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var replay = Result(existing);
                CareFailedPayoutReviewPolicy.RequireResult(replay, request);
                return replay;
            }

            var records = await ReadFailedPayoutReviewRecordsAsync(
                    connection, transaction, audit, request.ReviewId, cancellationToken)
                .ConfigureAwait(false);
            var record = records.SingleOrDefault() ?? throw new InvalidOperationException(
                "The failed-payout hold is unavailable or already has remediation lineage.");
            if (!string.Equals(
                    record.Review.ReviewSha256,
                    request.ReviewedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed-payout hold changed after review.");
            }

            var sequence = audit.Count == 0 ? 1 : checked(audit[^1].Sequence + 1);
            var previousSha256 = audit.Count == 0
                ? FailedPayoutAuditGenesis
                : audit[^1].RecordSha256;
            var disposition = new DurableCareFailedPayoutDispositionAuditRecord(
                sequence,
                request.ReviewId,
                nowUtc,
                operatorDigest,
                request.DispositionCode,
                request.ReviewedSha256,
                previousSha256,
                string.Empty);
            disposition = disposition with
            {
                RecordSha256 = FailedPayoutDispositionAuditSha256(disposition),
            };
            RequireFailedPayoutDispositionAuditRecord(disposition);

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO care_failed_payout_dispositions(
                    sequence, review_id, occurred_utc, operator_pseudonym_sha256,
                    disposition_code, reviewed_sha256, previous_record_sha256,
                    record_sha256)
                VALUES($sequence, $review, $occurred, $operator, $disposition,
                    $reviewed, $previous, $record);
                """;
            command.Parameters.AddWithValue("$sequence", disposition.Sequence);
            command.Parameters.AddWithValue("$review", disposition.ReviewId);
            command.Parameters.AddWithValue("$occurred", disposition.OccurredUtc.ToString("O"));
            command.Parameters.AddWithValue("$operator", disposition.OperatorPseudonymSha256);
            command.Parameters.AddWithValue("$disposition", disposition.DispositionCode);
            command.Parameters.AddWithValue("$reviewed", disposition.ReviewedSha256);
            command.Parameters.AddWithValue("$previous", disposition.PreviousRecordSha256);
            command.Parameters.AddWithValue("$record", disposition.RecordSha256);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The failed-payout decision append was incomplete.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var result = Result(disposition);
            CareFailedPayoutReviewPolicy.RequireResult(result, request);
            return result;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    internal async Task<IReadOnlyList<DurableCareFailedPayoutDispositionAuditRecord>>
        GetFailedPayoutDispositionAuditAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAndVerifyFailedPayoutDispositionAuditAsync(
                connection, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateFailedPayoutHoldReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceBatchId,
        int sourceOrdinal,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        if (createdUtc == default || createdUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A failed-payout hold reference requires an explicit UTC time.");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO care_failed_payout_holds(
                source_batch_id, source_ordinal, review_id, created_utc)
            VALUES($batch, $ordinal, $review, $created)
            ON CONFLICT(source_batch_id, source_ordinal) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$batch", sourceBatchId);
        command.Parameters.AddWithValue("$ordinal", sourceOrdinal);
        command.Parameters.AddWithValue(
            "$review", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackfillFailedPayoutHoldReferencesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item.batch_id, item.ordinal,
                   COALESCE(adjustment.occurred_utc, batch.updated_utc)
            FROM payout_item_results AS item
            INNER JOIN payout_batches AS batch ON batch.batch_id = item.batch_id
            LEFT JOIN payout_item_adjustments AS adjustment
                ON adjustment.batch_id = item.batch_id
               AND adjustment.ordinal = item.ordinal
            LEFT JOIN care_failed_payout_holds AS hold
                ON hold.source_batch_id = item.batch_id
               AND hold.source_ordinal = item.ordinal
            WHERE item.succeeded = 0 AND hold.review_id IS NULL
            ORDER BY item.batch_id, item.ordinal;
            """;
        var missing = new List<(string BatchId, int Ordinal, DateTimeOffset CreatedUtc)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (missing.Count >= CareOperationsPolicy.MaximumAggregateCount)
                {
                    throw new InvalidOperationException(
                        "The failed-payout hold migration exceeds its safety bound.");
                }

                missing.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    DateTimeOffset.Parse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture)));
            }
        }

        foreach (var item in missing)
        {
            await CreateFailedPayoutHoldReferenceAsync(
                    connection,
                    transaction,
                    item.BatchId,
                    item.Ordinal,
                    item.CreatedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireNoMissingFailedPayoutHoldReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*)
                 FROM payout_item_results AS item
                 LEFT JOIN care_failed_payout_holds AS hold
                    ON hold.source_batch_id = item.batch_id
                   AND hold.source_ordinal = item.ordinal
                 WHERE item.succeeded = 0 AND hold.review_id IS NULL) +
                (SELECT COUNT(*)
                 FROM care_failed_payout_holds AS hold
                 INNER JOIN payout_item_results AS item
                    ON item.batch_id = hold.source_batch_id
                   AND item.ordinal = hold.source_ordinal
                 WHERE item.succeeded != 0);
            """;
        if (Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
        {
            throw new InvalidOperationException(
                "A failed payout is missing its opaque hold reference.");
        }
    }

    private static async Task<IReadOnlyList<FailedPayoutReviewRecord>>
        ReadFailedPayoutReviewRecordsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<DurableCareFailedPayoutDispositionAuditRecord> audit,
            string? reviewId,
            CancellationToken cancellationToken)
    {
        var dispositions = audit.ToDictionary(item => item.ReviewId, StringComparer.Ordinal);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT hold.review_id, hold.source_batch_id, hold.source_ordinal,
                   batch.care_period_id, item.amount_cents, item.provider_status,
                   hold.created_utc, batch.result_sha256
            FROM care_failed_payout_holds AS hold
            INNER JOIN payout_item_results AS item
                ON item.batch_id = hold.source_batch_id
               AND item.ordinal = hold.source_ordinal
            INNER JOIN payout_batches AS batch
                ON batch.batch_id = hold.source_batch_id
            LEFT JOIN payout_item_remediations AS remediation
                ON remediation.source_batch_id = hold.source_batch_id
               AND remediation.source_ordinal = hold.source_ordinal
            WHERE item.succeeded = 0
              AND remediation.source_batch_id IS NULL
              AND ($review IS NULL OR hold.review_id = $review)
            ORDER BY hold.created_utc, hold.review_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$review", (object?)reviewId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", CareFailedPayoutReviewPolicy.MaximumReviews + 1);
        var result = new List<FailedPayoutReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var opaqueId = reader.GetString(0);
            var batchId = reader.GetString(1);
            var ordinal = reader.GetInt32(2);
            var periodId = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var amount = reader.GetInt64(4);
            var providerStatus = reader.GetString(5);
            var failedUtc = DateTimeOffset.Parse(
                reader.GetString(6), CultureInfo.InvariantCulture);
            var resultSha256 = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            dispositions.TryGetValue(opaqueId, out var disposition);
            var dispositionCode = disposition?.DispositionCode ??
                CareFailedPayoutReviewPolicy.Unreviewed;
            var noticeCode = disposition is null
                ? CareFailedPayoutReviewPolicy.NoticeNotReady
                : CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady;
            var failureCode = providerStatus is "RETURNED" or "REFUNDED" or "REVERSED"
                ? CareFailedPayoutReviewPolicy.Returned
                : CareFailedPayoutReviewPolicy.ProviderRejected;
            var digest = FailedPayoutReviewSha256(
                opaqueId,
                batchId,
                ordinal,
                periodId,
                amount,
                failureCode,
                failedUtc,
                resultSha256,
                noticeCode,
                dispositionCode);
            var review = new CareFailedPayoutReview(
                opaqueId,
                periodId,
                amount,
                failureCode,
                CareFailedPayoutReviewPolicy.FailedItemHold,
                noticeCode,
                failedUtc,
                digest,
                dispositionCode);
            result.Add(new FailedPayoutReviewRecord(batchId, ordinal, review));
        }

        CareFailedPayoutReviewPolicy.RequireList(result.Select(item => item.Review).ToArray());
        return result;
    }

    private static async Task<IReadOnlyList<DurableCareFailedPayoutDispositionAuditRecord>>
        ReadAndVerifyFailedPayoutDispositionAuditAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, review_id, occurred_utc, operator_pseudonym_sha256,
                   disposition_code, reviewed_sha256, previous_record_sha256,
                   record_sha256
            FROM care_failed_payout_dispositions
            ORDER BY sequence;
            """;
        var result = new List<DurableCareFailedPayoutDispositionAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (result.Count >= CareOperationsPolicy.MaximumAggregateCount)
            {
                throw new InvalidOperationException(
                    "The failed-payout disposition audit exceeds its safety bound.");
            }

            var record = new DurableCareFailedPayoutDispositionAuditRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(
                    reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7));
            RequireFailedPayoutDispositionAuditRecord(record);
            var expectedSequence = result.Count + 1L;
            var expectedPrevious = result.Count == 0
                ? FailedPayoutAuditGenesis
                : result[^1].RecordSha256;
            if (record.Sequence != expectedSequence ||
                !string.Equals(
                    record.PreviousRecordSha256,
                    expectedPrevious,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed-payout disposition audit chain is invalid.");
            }

            result.Add(record);
        }

        return result;
    }

    private static void RequireFailedPayoutDispositionAuditRecord(
        DurableCareFailedPayoutDispositionAuditRecord record)
    {
        CareFailedPayoutReviewPolicy.RequireRequest(new CareFailedPayoutDispositionRequest(
            record.ReviewId,
            record.ReviewedSha256,
            record.DispositionCode,
            ExplicitlyApproved: true));
        if (record.Sequence < 1 || record.OccurredUtc == default ||
            record.OccurredUtc.Offset != TimeSpan.Zero ||
            CareOperatorActionAuditIntegrity.NormalizeOperatorPseudonymSha256(
                record.OperatorPseudonymSha256) != record.OperatorPseudonymSha256 ||
            !IsLowerSha256(record.PreviousRecordSha256) ||
            !IsLowerSha256(record.RecordSha256) ||
            !string.Equals(
                FailedPayoutDispositionAuditSha256(record),
                record.RecordSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The failed-payout disposition audit record is invalid.");
        }
    }

    private static CareFailedPayoutDispositionResult Result(
        DurableCareFailedPayoutDispositionAuditRecord record) => new(
            record.ReviewId,
            record.DispositionCode,
            CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
            record.OccurredUtc,
            ProviderCalls: 0,
            FinancialMutations: 0,
            PayoutAttempts: 0,
            NotificationTransports: 0);

    private static string FailedPayoutReviewSha256(
        string reviewId,
        string sourceBatchId,
        int sourceOrdinal,
        string carePeriodId,
        long amountCents,
        string failureCode,
        DateTimeOffset failedUtc,
        string resultSha256,
        string noticeCode,
        string dispositionCode) => Digest(
            "care-failed-payout-review-v1",
            reviewId,
            sourceBatchId,
            sourceOrdinal.ToString(CultureInfo.InvariantCulture),
            carePeriodId,
            amountCents.ToString(CultureInfo.InvariantCulture),
            failureCode,
            failedUtc.ToString("O", CultureInfo.InvariantCulture),
            resultSha256,
            noticeCode,
            dispositionCode);

    private static string FailedPayoutDispositionAuditSha256(
        DurableCareFailedPayoutDispositionAuditRecord record) => Digest(
            "care-failed-payout-disposition-v1",
            record.Sequence.ToString(CultureInfo.InvariantCulture),
            record.ReviewId,
            record.OccurredUtc.ToString("O", CultureInfo.InvariantCulture),
            record.OperatorPseudonymSha256,
            record.DispositionCode,
            record.ReviewedSha256,
            record.PreviousRecordSha256);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Digest(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBytes, bytes.Length);
                hash.AppendData(lengthBytes);
                hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record FailedPayoutReviewRecord(
        string SourceBatchId,
        int SourceOrdinal,
        CareFailedPayoutReview Review);
}
