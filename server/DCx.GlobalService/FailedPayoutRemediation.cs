using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record FailedPayoutRemediationItemReview(
    int SourceOrdinal,
    string ContributorPseudonym,
    long AmountCents,
    string SourceProviderStatus,
    string SourceRecipientSnapshotId,
    string SourceDestinationSha256,
    string ReplacementRecipientSnapshotId,
    string ReplacementDestinationSha256);

public sealed record FailedPayoutRemediationReview(
    string SourceBatchId,
    string SourceResultSha256,
    PayoutBatchPreview RemediationPreview,
    IReadOnlyList<FailedPayoutRemediationItemReview> Items,
    DateTimeOffset ReviewedUtc,
    DateTimeOffset ExpiresUtc,
    string ReviewSha256);

public sealed record DurablePayoutItemRemediation(
    string SourceBatchId,
    int SourceOrdinal,
    string RemediationBatchId,
    int RemediationOrdinal,
    string ContributorPseudonym,
    long AmountCents,
    string SourceProviderStatus,
    string ReplacementRecipientSnapshotId,
    string ReplacementDestinationSha256,
    string SourceResultSha256,
    string ReviewSha256,
    DateTimeOffset ApprovedUtc,
    string RemediationStatus);

public sealed partial class DurableCareRepository
{
    private static readonly TimeSpan FailedPayoutRemediationReviewLifetime = TimeSpan.FromMinutes(10);

    public async Task<FailedPayoutRemediationReview> ReviewFailedPayoutRemediationAsync(
        string sourceBatchId,
        PayoutBatchPreview remediationPreview,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateRemediationPreview(sourceBatchId, remediationPreview);
        var reviewedUtc = now.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadFailedPayoutSourceAsync(
                connection, null, sourceBatchId, cancellationToken)
            .ConfigureAwait(false);
        await RequireTargetBatchAbsentAsync(
                connection, null, remediationPreview.BatchId, cancellationToken)
            .ConfigureAwait(false);
        var items = await BuildRemediationItemsAsync(
                connection, null, source, remediationPreview, cancellationToken)
            .ConfigureAwait(false);
        var expiresUtc = reviewedUtc.Add(FailedPayoutRemediationReviewLifetime);
        var reviewSha = FailedPayoutRemediationReviewSha(
            sourceBatchId,
            source.ResultSha256,
            remediationPreview,
            items,
            reviewedUtc,
            expiresUtc);
        return new FailedPayoutRemediationReview(
            sourceBatchId,
            source.ResultSha256,
            remediationPreview,
            items,
            reviewedUtc,
            expiresUtc,
            reviewSha);
    }

    public async Task<bool> ReserveFailedPayoutRemediationAsync(
        FailedPayoutRemediationReview review,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateRemediationReview(review, accountEligibilityVerified, ownerApproved, today, now);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var sqliteTransaction = transaction;
            var source = await ReadFailedPayoutSourceAsync(
                    connection, sqliteTransaction, review.SourceBatchId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(source.ResultSha256, review.SourceResultSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed payout changed after remediation review.");
            }

            var existing = await ReadExistingRemediationTargetAsync(
                    connection,
                    sqliteTransaction,
                    review.RemediationPreview.BatchId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (string.Equals(existing.PreviewSha256, PayoutPreviewSha(review.RemediationPreview), StringComparison.Ordinal) &&
                    string.Equals(existing.ReviewSha256, review.ReviewSha256, StringComparison.Ordinal) &&
                    existing.ItemCount == review.Items.Count)
                {
                    return false;
                }

                throw new InvalidOperationException(
                    "The failed payout remediation batch conflicts with prior state.");
            }

            var currentItems = await BuildRemediationItemsAsync(
                    connection,
                    sqliteTransaction,
                    source,
                    review.RemediationPreview,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!currentItems.SequenceEqual(review.Items))
            {
                throw new InvalidOperationException(
                    "The failed payout remediation no longer matches its reviewed items.");
            }

            var availableLiability = await ReadUnreservedContributorPayableCentsAsync(
                    connection, sqliteTransaction, cancellationToken)
                .ConfigureAwait(false);
            if (review.RemediationPreview.TotalCents > availableLiability)
            {
                throw new InvalidOperationException(
                    "The failed payout remediation exceeds the unreserved contributor payable liability.");
            }

            var approvedUtc = now.ToUniversalTime();
            var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = """
                INSERT INTO payout_batches(
                    batch_id, preview_sha256, preview_json, preview_total_cents, status,
                    remediation_review_sha256, created_utc, updated_utc)
                VALUES($batch, $sha, $json, $preview_total, 'reserved',
                    $remediation_review, $created, $updated);
                """;
            command.Parameters.AddWithValue("$batch", review.RemediationPreview.BatchId);
            command.Parameters.AddWithValue("$sha", PayoutPreviewSha(review.RemediationPreview));
            command.Parameters.AddWithValue(
                "$json", JsonSerializer.Serialize(review.RemediationPreview));
            command.Parameters.AddWithValue("$preview_total", review.RemediationPreview.TotalCents);
            command.Parameters.AddWithValue("$remediation_review", review.ReviewSha256);
            command.Parameters.AddWithValue("$created", approvedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", approvedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            for (var remediationOrdinal = 0; remediationOrdinal < review.Items.Count; remediationOrdinal++)
            {
                var item = review.Items[remediationOrdinal];
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO payout_item_remediations(
                        source_batch_id, source_ordinal,
                        remediation_batch_id, remediation_ordinal,
                        contributor_pseudonym, amount_cents, source_provider_status,
                        replacement_recipient_snapshot_id, replacement_destination_sha256,
                        source_result_sha256, review_sha256, approved_utc)
                    VALUES($source_batch, $source_ordinal,
                        $remediation_batch, $remediation_ordinal,
                        $contributor, $amount, $source_status,
                        $replacement_snapshot, $replacement_destination,
                        $source_result, $review_sha, $approved);
                    """;
                command.Parameters.AddWithValue("$source_batch", review.SourceBatchId);
                command.Parameters.AddWithValue("$source_ordinal", item.SourceOrdinal);
                command.Parameters.AddWithValue("$remediation_batch", review.RemediationPreview.BatchId);
                command.Parameters.AddWithValue("$remediation_ordinal", remediationOrdinal);
                command.Parameters.AddWithValue("$contributor", item.ContributorPseudonym);
                command.Parameters.AddWithValue("$amount", item.AmountCents);
                command.Parameters.AddWithValue("$source_status", item.SourceProviderStatus);
                command.Parameters.AddWithValue(
                    "$replacement_snapshot", item.ReplacementRecipientSnapshotId);
                command.Parameters.AddWithValue(
                    "$replacement_destination", item.ReplacementDestinationSha256);
                command.Parameters.AddWithValue("$source_result", review.SourceResultSha256);
                command.Parameters.AddWithValue("$review_sha", review.ReviewSha256);
                command.Parameters.AddWithValue("$approved", approvedUtc.ToString("O"));
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    throw new InvalidOperationException(
                        "A failed payout item already has a remediation or the reviewed lineage conflicts.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<IReadOnlyList<DurablePayoutItemRemediation>>
        GetPayoutItemRemediationsAsync(
            string sourceBatchId,
            CancellationToken cancellationToken = default)
    {
        PayPalSandboxValidation.RequireLocalBatchId(sourceBatchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT remediation.source_ordinal,
                   remediation.remediation_batch_id, remediation.remediation_ordinal,
                   remediation.contributor_pseudonym, remediation.amount_cents,
                   remediation.source_provider_status,
                   remediation.replacement_recipient_snapshot_id,
                   remediation.replacement_destination_sha256,
                   remediation.source_result_sha256, remediation.review_sha256,
                   remediation.approved_utc, target.status
            FROM payout_item_remediations AS remediation
            INNER JOIN payout_batches AS target
                ON target.batch_id = remediation.remediation_batch_id
            WHERE remediation.source_batch_id = $source
            ORDER BY remediation.source_ordinal;
            """;
        command.Parameters.AddWithValue("$source", sourceBatchId);
        var result = new List<DurablePayoutItemRemediation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = new DurablePayoutItemRemediation(
                sourceBatchId,
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10)),
                reader.GetString(11));
            ValidateStoredRemediation(item);
            result.Add(item);
        }

        return result;
    }

    private static void ValidateRemediationPreview(
        string sourceBatchId,
        PayoutBatchPreview remediationPreview)
    {
        PayPalSandboxValidation.RequireLocalBatchId(sourceBatchId);
        PayoutContractValidation.RequirePreview(remediationPreview);
        if (string.Equals(sourceBatchId, remediationPreview.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A failed payout remediation requires a distinct batch identifier.");
        }
    }

    private static void ValidateRemediationReview(
        FailedPayoutRemediationReview review,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(review);
        ValidateRemediationPreview(review.SourceBatchId, review.RemediationPreview);
        ValidatePayout(
            review.RemediationPreview,
            accountEligibilityVerified,
            ownerApproved,
            today);
        if (!PayoutContractValidation.IsLowerSha256(review.SourceResultSha256) ||
            !PayoutContractValidation.IsLowerSha256(review.ReviewSha256) ||
            review.Items is null || review.Items.Count != review.RemediationPreview.Items.Count ||
            review.ReviewedUtc.Offset != TimeSpan.Zero || review.ExpiresUtc.Offset != TimeSpan.Zero ||
            review.ExpiresUtc - review.ReviewedUtc != FailedPayoutRemediationReviewLifetime ||
            now.ToUniversalTime() < review.ReviewedUtc || now.ToUniversalTime() > review.ExpiresUtc)
        {
            throw new InvalidOperationException(
                "The failed payout remediation review is invalid or expired.");
        }

        for (var index = 0; index < review.Items.Count; index++)
        {
            var item = review.Items[index];
            var target = review.RemediationPreview.Items[index];
            if (item.SourceOrdinal < 0 || item.AmountCents != target.AmountCents ||
                !string.Equals(item.ContributorPseudonym, target.ContributorPseudonym, StringComparison.Ordinal) ||
                !string.Equals(item.ReplacementRecipientSnapshotId, target.RecipientApproval.SnapshotId, StringComparison.Ordinal) ||
                !string.Equals(item.ReplacementDestinationSha256, target.RecipientApproval.DestinationSha256, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.SourceProviderStatus) ||
                item.SourceProviderStatus.Length > 128 || item.SourceProviderStatus.Any(char.IsControl) ||
                !PayoutContractValidation.IsLowerSha256(item.SourceDestinationSha256))
            {
                throw new InvalidOperationException(
                    "The failed payout remediation item review is invalid.");
            }

            PayoutContractValidation.RequireSnapshotId(item.SourceRecipientSnapshotId);
        }

        var expectedSha = FailedPayoutRemediationReviewSha(
            review.SourceBatchId,
            review.SourceResultSha256,
            review.RemediationPreview,
            review.Items,
            review.ReviewedUtc,
            review.ExpiresUtc);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedSha),
                Convert.FromHexString(review.ReviewSha256)))
        {
            throw new InvalidOperationException(
                "The failed payout remediation review digest is invalid.");
        }
    }

    private static async Task<IReadOnlyList<FailedPayoutRemediationItemReview>>
        BuildRemediationItemsAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            FailedPayoutSourceState source,
            PayoutBatchPreview remediationPreview,
            CancellationToken cancellationToken)
    {
        if (remediationPreview.EligibilityDate != source.Preview.EligibilityDate)
        {
            throw new InvalidOperationException(
                "A failed payout remediation must retain the source Net 60 eligibility date.");
        }

        var byContributor = source.Items.ToDictionary(
            item => item.ContributorPseudonym,
            StringComparer.Ordinal);
        var reviewItems = new List<FailedPayoutRemediationItemReview>(remediationPreview.Items.Count);
        for (var remediationOrdinal = 0;
            remediationOrdinal < remediationPreview.Items.Count;
            remediationOrdinal++)
        {
            var replacement = remediationPreview.Items[remediationOrdinal];
            if (!byContributor.TryGetValue(replacement.ContributorPseudonym, out var sourceItem) ||
                sourceItem.Succeeded || sourceItem.AmountCents != replacement.AmountCents)
            {
                throw new InvalidOperationException(
                    "A remediation may include only exact unsuccessful source payout items.");
            }

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM payout_item_remediations
                WHERE source_batch_id = $batch AND source_ordinal = $ordinal;
                """;
            command.Parameters.AddWithValue("$batch", source.Preview.BatchId);
            command.Parameters.AddWithValue("$ordinal", sourceItem.Ordinal);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                throw new InvalidOperationException(
                    "A failed payout item already has a remediation batch.");
            }

            reviewItems.Add(new FailedPayoutRemediationItemReview(
                sourceItem.Ordinal,
                sourceItem.ContributorPseudonym,
                sourceItem.AmountCents,
                sourceItem.ProviderStatus,
                sourceItem.RecipientSnapshotId,
                sourceItem.DestinationSha256,
                replacement.RecipientApproval.SnapshotId,
                replacement.RecipientApproval.DestinationSha256));
        }

        return reviewItems.ToArray();
    }

    private static async Task<FailedPayoutSourceState> ReadFailedPayoutSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceBatchId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT preview_sha256, preview_json, status, provider_status,
                   result_sha256, paid_cents, preview_total_cents
            FROM payout_batches WHERE batch_id = $batch;
            """;
        command.Parameters.AddWithValue("$batch", sourceBatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The source payout batch does not exist.");
        }

        var previewSha = reader.GetString(0);
        var previewJson = reader.GetString(1);
        var status = reader.GetString(2);
        var providerStatus = reader.IsDBNull(3) ? null : reader.GetString(3);
        var resultSha = reader.IsDBNull(4) ? null : reader.GetString(4);
        var paidCents = reader.GetInt64(5);
        var previewTotalCents = reader.GetInt64(6);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (status is not ("failed" or "partially_succeeded") ||
            string.IsNullOrWhiteSpace(providerStatus) ||
            !PayoutContractValidation.IsLowerSha256(resultSha))
        {
            throw new InvalidOperationException(
                "Only a terminal payout with unsuccessful items can be remediated.");
        }
        var durableResultSha = resultSha!;

        PayoutBatchPreview preview;
        try
        {
            preview = JsonSerializer.Deserialize<PayoutBatchPreview>(previewJson)
                ?? throw new InvalidOperationException("The source payout preview is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The source payout preview is unreadable.");
        }

        PayoutContractValidation.RequirePreview(preview);
        if (!string.Equals(preview.BatchId, sourceBatchId, StringComparison.Ordinal) ||
            !string.Equals(PayoutPreviewSha(preview), previewSha, StringComparison.Ordinal) ||
            previewTotalCents != preview.TotalCents)
        {
            throw new InvalidOperationException(
                "The source payout preview failed its stored binding.");
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT ordinal, contributor_pseudonym, amount_cents,
                   recipient_snapshot_id, destination_sha256, succeeded, provider_status
            FROM payout_item_results
            WHERE batch_id = $batch
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$batch", sourceBatchId);
        var items = new List<FailedPayoutSourceItem>();
        await using (var itemReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new FailedPayoutSourceItem(
                    itemReader.GetInt32(0),
                    itemReader.GetString(1),
                    itemReader.GetInt64(2),
                    itemReader.GetString(3),
                    itemReader.GetString(4),
                    itemReader.GetInt32(5) == 1,
                    itemReader.GetString(6)));
            }
        }

        if (items.Count != preview.Items.Count || items.All(item => item.Succeeded))
        {
            throw new InvalidOperationException(
                "The source payout item state is incomplete or has no failed item.");
        }

        for (var ordinal = 0; ordinal < items.Count; ordinal++)
        {
            var stored = items[ordinal];
            var approved = preview.Items[ordinal];
            if (stored.Ordinal != ordinal ||
                !string.Equals(stored.ContributorPseudonym, approved.ContributorPseudonym, StringComparison.Ordinal) ||
                stored.AmountCents != approved.AmountCents ||
                !string.Equals(stored.RecipientSnapshotId, approved.RecipientApproval.SnapshotId, StringComparison.Ordinal) ||
                !string.Equals(stored.DestinationSha256, approved.RecipientApproval.DestinationSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The source payout item state conflicts with its approved preview.");
            }
        }

        var outcome = items.Any(item => item.Succeeded)
            ? PayoutBatchOutcome.PartiallySucceeded
            : PayoutBatchOutcome.Failed;
        if ((status == "failed") != (outcome == PayoutBatchOutcome.Failed))
        {
            throw new InvalidOperationException(
                "The source payout terminal state conflicts with its item results.");
        }

        var storedResult = new PayoutBatchResult(
            sourceBatchId,
            outcome,
            providerStatus,
            items.Select(item => new PayoutItemResult(
                item.ContributorPseudonym,
                item.AmountCents,
                item.Succeeded,
                item.ProviderStatus)).ToArray());
        var normalized = PayoutContractValidation.NormalizeTerminalResult(preview, storedResult);
        if (!string.Equals(PayoutResultSha(storedResult, normalized), durableResultSha, StringComparison.Ordinal) ||
            items.Where(item => item.Succeeded).Sum(item => item.AmountCents) != paidCents)
        {
            throw new InvalidOperationException(
                "The source payout result failed its durable integrity check.");
        }

        return new FailedPayoutSourceState(preview, durableResultSha, items);
    }

    private static async Task RequireTargetBatchAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM payout_batches WHERE batch_id = $batch;";
        command.Parameters.AddWithValue("$batch", batchId);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
        {
            throw new InvalidOperationException(
                "The failed payout remediation batch identifier is already in use.");
        }
    }

    private static async Task ValidateRemediationTargetLineageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT remediation_review_sha256
            FROM payout_batches WHERE batch_id = $batch;
            """;
        command.Parameters.AddWithValue("$batch", preview.BatchId);
        var markerValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (markerValue is null)
        {
            throw new InvalidOperationException(
                "The payout batch disappeared during remediation validation.");
        }

        var marker = markerValue is DBNull ? null : (string)markerValue;
        command.Parameters.Clear();
        command.CommandText = """
            SELECT source_batch_id, source_ordinal, remediation_ordinal,
                   contributor_pseudonym, amount_cents, source_provider_status,
                   replacement_recipient_snapshot_id, replacement_destination_sha256,
                   source_result_sha256, review_sha256, approved_utc
            FROM payout_item_remediations
            WHERE remediation_batch_id = $batch
            ORDER BY remediation_ordinal;
            """;
        command.Parameters.AddWithValue("$batch", preview.BatchId);
        var rows = new List<RemediationTargetLineage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new RemediationTargetLineage(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    DateTimeOffset.Parse(reader.GetString(10))));
            }
        }

        if (marker is null && rows.Count == 0) return;
        if (!PayoutContractValidation.IsLowerSha256(marker) ||
            rows.Count != preview.Items.Count || rows.Count == 0 ||
            rows.Select(row => row.SourceBatchId).Distinct(StringComparer.Ordinal).Count() != 1 ||
            rows.Any(row => !string.Equals(row.ReviewSha256, marker, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The failed payout remediation authorization is missing or inconsistent.");
        }

        for (var ordinal = 0; ordinal < rows.Count; ordinal++)
        {
            var row = rows[ordinal];
            var target = preview.Items[ordinal];
            if (row.RemediationOrdinal != ordinal || row.SourceOrdinal < 0 ||
                string.Equals(row.SourceBatchId, preview.BatchId, StringComparison.Ordinal) ||
                !string.Equals(row.ContributorPseudonym, target.ContributorPseudonym, StringComparison.Ordinal) ||
                row.AmountCents != target.AmountCents ||
                !string.Equals(row.ReplacementRecipientSnapshotId, target.RecipientApproval.SnapshotId, StringComparison.Ordinal) ||
                !string.Equals(row.ReplacementDestinationSha256, target.RecipientApproval.DestinationSha256, StringComparison.Ordinal) ||
                !PayoutContractValidation.IsLowerSha256(row.SourceResultSha256) ||
                row.ApprovedUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    "The failed payout remediation target conflicts with its authorization.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                SELECT contributor_pseudonym, amount_cents, succeeded, provider_status
                FROM payout_item_results
                WHERE batch_id = $source_batch AND ordinal = $source_ordinal;
                """;
            command.Parameters.AddWithValue("$source_batch", row.SourceBatchId);
            command.Parameters.AddWithValue("$source_ordinal", row.SourceOrdinal);
            await using var sourceReader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(sourceReader.GetString(0), row.ContributorPseudonym, StringComparison.Ordinal) ||
                sourceReader.GetInt64(1) != row.AmountCents ||
                sourceReader.GetInt32(2) != 0 ||
                !string.Equals(sourceReader.GetString(3), row.SourceProviderStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed payout remediation source item changed or is no longer unsuccessful.");
            }
        }
    }

    private static async Task<ExistingRemediationTarget?> ReadExistingRemediationTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target.preview_sha256, target.remediation_review_sha256,
                   MIN(remediation.review_sha256), MAX(remediation.review_sha256),
                   COUNT(remediation.source_ordinal)
            FROM payout_batches AS target
            LEFT JOIN payout_item_remediations AS remediation
                ON remediation.remediation_batch_id = target.batch_id
            WHERE target.batch_id = $batch
            GROUP BY target.preview_sha256;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var storedReview = reader.IsDBNull(1) ? null : reader.GetString(1);
        var minimumReview = reader.IsDBNull(2) ? null : reader.GetString(2);
        var maximumReview = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new ExistingRemediationTarget(
            reader.GetString(0),
            string.Equals(storedReview, minimumReview, StringComparison.Ordinal) &&
                string.Equals(minimumReview, maximumReview, StringComparison.Ordinal)
                ? minimumReview
                : null,
            reader.GetInt32(4));
    }

    private static string FailedPayoutRemediationReviewSha(
        string sourceBatchId,
        string sourceResultSha256,
        PayoutBatchPreview remediationPreview,
        IReadOnlyList<FailedPayoutRemediationItemReview> items,
        DateTimeOffset reviewedUtc,
        DateTimeOffset expiresUtc) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            SourceBatchId = sourceBatchId,
            SourceResultSha256 = sourceResultSha256,
            RemediationPreview = remediationPreview,
            Items = items,
            ReviewedUtc = reviewedUtc.ToUniversalTime(),
            ExpiresUtc = expiresUtc.ToUniversalTime(),
        })));

    private static void ValidateStoredRemediation(DurablePayoutItemRemediation item)
    {
        PayPalSandboxValidation.RequireLocalBatchId(item.SourceBatchId);
        PayPalSandboxValidation.RequireLocalBatchId(item.RemediationBatchId);
        PayoutContractValidation.RequireSnapshotId(item.ReplacementRecipientSnapshotId);
        if (item.SourceOrdinal < 0 || item.RemediationOrdinal < 0 || item.AmountCents <= 0 ||
            string.IsNullOrWhiteSpace(item.ContributorPseudonym) ||
            string.IsNullOrWhiteSpace(item.SourceProviderStatus) ||
            !PayoutContractValidation.IsLowerSha256(item.ReplacementDestinationSha256) ||
            !PayoutContractValidation.IsLowerSha256(item.SourceResultSha256) ||
            !PayoutContractValidation.IsLowerSha256(item.ReviewSha256) ||
            item.ApprovedUtc.Offset != TimeSpan.Zero ||
            item.RemediationStatus is not ("reserved" or "succeeded" or "failed" or "partially_succeeded" or "review_required"))
        {
            throw new InvalidOperationException(
                "The stored failed payout remediation is invalid.");
        }
    }

    private sealed record FailedPayoutSourceItem(
        int Ordinal,
        string ContributorPseudonym,
        long AmountCents,
        string RecipientSnapshotId,
        string DestinationSha256,
        bool Succeeded,
        string ProviderStatus);

    private sealed record FailedPayoutSourceState(
        PayoutBatchPreview Preview,
        string ResultSha256,
        IReadOnlyList<FailedPayoutSourceItem> Items);

    private sealed record ExistingRemediationTarget(
        string PreviewSha256,
        string? ReviewSha256,
        int ItemCount);

    private sealed record RemediationTargetLineage(
        string SourceBatchId,
        int SourceOrdinal,
        int RemediationOrdinal,
        string ContributorPseudonym,
        long AmountCents,
        string SourceProviderStatus,
        string ReplacementRecipientSnapshotId,
        string ReplacementDestinationSha256,
        string SourceResultSha256,
        string ReviewSha256,
        DateTimeOffset ApprovedUtc);
}

public sealed partial class DurablePayoutCoordinator
{
    public async Task<PayoutBatchResult> ExecuteFailedItemRemediationAsync(
        FailedPayoutRemediationReview review,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await repository.ReserveFailedPayoutRemediationAsync(
                review,
                accountEligibilityVerified,
                ownerApproved,
                today,
                now,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The failed payout remediation was already approved, reserved or executed.");
        }

        var result = await provider.SendSandboxAsync(
                review.RemediationPreview, cancellationToken)
            .ConfigureAwait(false);
        PayoutContractValidation.ValidateProviderResult(review.RemediationPreview, result);
        if (result.IsTerminal)
        {
            await repository.CompletePayoutAsync(
                    review.RemediationPreview, result, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}
