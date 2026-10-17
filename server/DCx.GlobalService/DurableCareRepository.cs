using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record DurablePayoutState(
    string BatchId,
    string Status,
    string? ProviderStatus,
    DateTimeOffset UpdatedUtc,
    long PaidCents = 0);

public sealed record DurablePayoutItemState(
    string BatchId,
    string ContributorPseudonym,
    long AmountCents,
    string RecipientSnapshotId,
    string DestinationSha256,
    bool Succeeded,
    string ProviderStatus);

public sealed record DurablePayoutItemAdjustment(
    string BatchId,
    int Ordinal,
    string ContributorPseudonym,
    long AmountCents,
    string FromProviderStatus,
    string ToProviderStatus,
    string LedgerEventId,
    DateTimeOffset OccurredUtc);

public sealed record DurablePendingPayout(
    PayoutBatchPreview Preview,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record DurableTerminalPayout(
    PayoutBatchPreview Preview,
    DateTimeOffset UpdatedUtc);

public sealed record PayoutReconciliationSummary(
    int Scanned,
    int Completed,
    int StillPending,
    int Resubmitted);

public sealed record PayoutTerminalReconciliationSummary(
    int Scanned,
    int Corrected,
    int Unchanged,
    int ProviderPending);

public sealed record DurableWebhookAuditRecord(
    string EventId,
    string TransmissionId,
    string EventType,
    string SubscriptionSha256,
    string PayloadSha256,
    DateTimeOffset OccurredUtc,
    string DecisionCode);

public sealed partial class DurableCareRepository : IPayPalSandboxPayoutReferenceStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private DurableCareRepository(string root)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public static async Task<DurableCareRepository> OpenAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var canonical = Path.GetFullPath(root);
        Directory.CreateDirectory(canonical);
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The CARE ledger root cannot be a reparse point.");
        }

        var repository = new DurableCareRepository(canonical);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return repository;
    }

    public async Task<bool> PostAsync(
        LedgerJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var inserted = await PostInternalAsync(connection, (SqliteTransaction)transaction, entry, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<bool> ProcessSandboxWebhookAsync(
        PayPalSandboxWebhookEvent webhook,
        string signatureBase64,
        byte[] verificationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!PayPalSandboxWebhookIntegrity.Verify(webhook, signatureBase64, verificationKey))
        {
            throw new InvalidOperationException("The sandbox webhook signature is invalid.");
        }

        ValidateWebhook(webhook);
        var payloadSha = Convert.ToHexStringLower(SHA256.HashData(PayPalSandboxWebhookIntegrity.CanonicalBytes(webhook)));
        return await ProcessWebhookAsync(
                webhook,
                payloadSha,
                null,
                null,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> ProcessOfficialSandboxWebhookAsync(
        PayPalSandboxHttpAdapter adapter,
        PayPalSandboxWebhookVerificationEvidence evidence,
        ReadOnlyMemory<byte> webhookEventJson,
        string expectedPlanId,
        CancellationToken cancellationToken = default) =>
        ProcessOfficialSandboxWebhookAsync(
            adapter,
            evidence,
            webhookEventJson,
            expectedPlanId,
            DateTimeOffset.UtcNow,
            cancellationToken);

    public async Task<bool> ProcessOfficialSandboxWebhookAsync(
        PayPalSandboxHttpAdapter adapter,
        PayPalSandboxWebhookVerificationEvidence evidence,
        ReadOnlyMemory<byte> webhookEventJson,
        string expectedPlanId,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        PayPalSandboxValidation.RequireSubscriptionPlanId(expectedPlanId);
        var verified = await adapter.VerifyAndMapWebhookAsync(
                evidence, webhookEventJson, cancellationToken)
            .ConfigureAwait(false);
        return await ProcessWebhookAsync(
                null,
                verified.PayloadSha256,
                verified,
                expectedPlanId,
                receivedUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ProcessWebhookAsync(
        PayPalSandboxWebhookEvent? fixtureWebhook,
        string payloadSha,
        PayPalSandboxVerifiedWebhook? verifiedWebhook,
        string? expectedPlanId,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken)
    {
        var eventId = verifiedWebhook?.EventId ?? fixtureWebhook!.EventId;
        var transmissionId = verifiedWebhook?.TransmissionId ?? fixtureWebhook!.TransmissionId;
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadWebhookAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    eventId,
                    transmissionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.EventId == eventId && existing.TransmissionId == transmissionId &&
                    string.Equals(existing.PayloadSha256, payloadSha, StringComparison.Ordinal))
                {
                    return false;
                }

                throw new InvalidOperationException("A webhook event or transmission identifier conflicts with prior data.");
            }

            var quarantined = await ReadWebhookQuarantineIdentityAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    eventId,
                    transmissionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (quarantined is not null)
            {
                if (verifiedWebhook is not null &&
                    quarantined.EventId == eventId &&
                    quarantined.TransmissionId == transmissionId &&
                    string.Equals(quarantined.PayloadSha256, payloadSha, StringComparison.Ordinal))
                {
                    return false;
                }

                throw new InvalidOperationException(
                    "A quarantined webhook event or transmission identifier conflicts with prior data.");
            }

            PayPalSandboxWebhookEvent webhook;
            try
            {
                webhook = fixtureWebhook ?? await NormalizeOfficialWebhookAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        verifiedWebhook!,
                        expectedPlanId!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PayPalSandboxException exception) when (
                verifiedWebhook is not null && IsQuarantinableWebhookDependency(exception.Code))
            {
                await InsertWebhookQuarantineAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        verifiedWebhook,
                        expectedPlanId!,
                        exception.Code,
                        receivedUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            await ApplyNormalizedWebhookAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    webhook,
                    payloadSha,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<bool> ReservePayoutAsync(
        PayoutBatchPreview preview,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidatePayout(preview, accountEligibilityVerified, ownerApproved, today);
        var previewSha = PayoutPreviewSha(preview);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT preview_sha256 FROM payout_batches WHERE batch_id = $batch;";
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            var existing = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing, previewSha, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The payout batch ID conflicts with another preview.");
                }

                return false;
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO payout_batches(batch_id, preview_sha256, preview_json, status, created_utc, updated_utc)
                VALUES($batch, $sha, $json, 'reserved', $created, $updated);
                """;
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            command.Parameters.AddWithValue("$sha", previewSha);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview));
            command.Parameters.AddWithValue("$created", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<bool> CompletePayoutAsync(
        PayoutBatchPreview preview,
        PayoutBatchResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedItems = PayoutContractValidation.NormalizeTerminalResult(preview, result);
        var resultSha = PayoutResultSha(result, normalizedItems);
        var paidCents = normalizedItems.Where(item => item.Succeeded).Sum(item => item.AmountCents);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT preview_sha256, status, provider_status, result_sha256 FROM payout_batches WHERE batch_id = $batch;";
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The payout batch was not reserved.");
            }

            var sha = reader.GetString(0);
            var status = reader.GetString(1);
            var providerStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
            var existingResultSha = reader.IsDBNull(3) ? null : reader.GetString(3);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!string.Equals(sha, PayoutPreviewSha(preview), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The reserved payout preview was modified.");
            }
            await ValidateRemediationTargetLineageAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    preview,
                    cancellationToken)
                .ConfigureAwait(false);

            var targetStatus = result.Outcome switch
            {
                PayoutBatchOutcome.Succeeded => "succeeded",
                PayoutBatchOutcome.Failed => "failed",
                PayoutBatchOutcome.PartiallySucceeded => "partially_succeeded",
                _ => throw new InvalidOperationException("A pending payout cannot be completed."),
            };
            if (status != "reserved")
            {
                if (status == targetStatus && providerStatus == result.ProviderStatus &&
                    existingResultSha == resultSha)
                {
                    return false;
                }

                throw new InvalidOperationException("The payout batch has a conflicting terminal state.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE payout_batches
                SET status = $status, provider_status = $provider,
                    result_sha256 = $result_sha, paid_cents = $paid, updated_utc = $updated
                WHERE batch_id = $batch AND status = 'reserved';
                """;
            command.Parameters.AddWithValue("$status", targetStatus);
            command.Parameters.AddWithValue("$provider", result.ProviderStatus);
            command.Parameters.AddWithValue("$result_sha", resultSha);
            command.Parameters.AddWithValue("$paid", paidCents);
            command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The payout terminal transition conflicted.");
            }

            for (var index = 0; index < preview.Items.Count; index++)
            {
                var approved = preview.Items[index];
                var itemResult = normalizedItems[index];
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO payout_item_results(
                        batch_id, ordinal, contributor_pseudonym, amount_cents,
                        recipient_snapshot_id, destination_sha256, succeeded, provider_status)
                    VALUES($batch, $ordinal, $contributor, $amount,
                        $snapshot, $destination_sha, $succeeded, $provider);
                    """;
                command.Parameters.AddWithValue("$batch", preview.BatchId);
                command.Parameters.AddWithValue("$ordinal", index);
                command.Parameters.AddWithValue("$contributor", approved.ContributorPseudonym);
                command.Parameters.AddWithValue("$amount", approved.AmountCents);
                command.Parameters.AddWithValue("$snapshot", approved.RecipientApproval.SnapshotId);
                command.Parameters.AddWithValue(
                    "$destination_sha", approved.RecipientApproval.DestinationSha256);
                command.Parameters.AddWithValue("$succeeded", itemResult.Succeeded ? 1 : 0);
                command.Parameters.AddWithValue("$provider", itemResult.ProviderStatus);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (paidCents > 0)
            {
                await PostInternalAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        CareLedgerJournal.SandboxPayout(
                            $"payout:{preview.BatchId}", paidCents, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<IReadOnlyList<DurablePendingPayout>> GetPendingPayoutsAsync(
        DateTimeOffset updatedBeforeOrAt,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preview_sha256, preview_json, created_utc, updated_utc
            FROM payout_batches
            WHERE status = 'reserved' AND updated_utc <= $cutoff
            ORDER BY updated_utc, batch_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", updatedBeforeOrAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        var pending = new List<DurablePendingPayout>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                PayoutBatchPreview preview;
                try
                {
                    preview = JsonSerializer.Deserialize<PayoutBatchPreview>(reader.GetString(1))
                        ?? throw new InvalidOperationException("A durable payout preview is empty.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"A durable payout preview is unreadable: {exception.GetType().Name}.");
                }

                if (!string.Equals(reader.GetString(0), PayoutPreviewSha(preview), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A durable payout preview failed its stored digest.");
                }

                pending.Add(new DurablePendingPayout(
                    preview,
                    DateTimeOffset.Parse(reader.GetString(2)),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }
        }

        foreach (var item in pending)
        {
            await ValidateRemediationTargetLineageAsync(
                    connection, null, item.Preview, cancellationToken)
                .ConfigureAwait(false);
        }

        return pending;
    }

    public async Task<IReadOnlyList<DurableTerminalPayout>> GetTerminalPayoutsAsync(
        DateTimeOffset updatedBeforeOrAt,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preview_sha256, preview_json, updated_utc
            FROM payout_batches
            WHERE status IN ('succeeded', 'partially_succeeded') AND updated_utc <= $cutoff
            ORDER BY updated_utc, batch_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", updatedBeforeOrAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        var terminal = new List<DurableTerminalPayout>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                PayoutBatchPreview preview;
                try
                {
                    preview = JsonSerializer.Deserialize<PayoutBatchPreview>(reader.GetString(1))
                        ?? throw new InvalidOperationException("A durable payout preview is empty.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"A durable payout preview is unreadable: {exception.GetType().Name}.");
                }

                if (!string.Equals(reader.GetString(0), PayoutPreviewSha(preview), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A durable payout preview failed its stored digest.");
                }

                terminal.Add(new DurableTerminalPayout(
                    preview,
                    DateTimeOffset.Parse(reader.GetString(2))));
            }
        }

        foreach (var item in terminal)
        {
            await ValidateRemediationTargetLineageAsync(
                    connection, null, item.Preview, cancellationToken)
                .ConfigureAwait(false);
        }

        return terminal;
    }

    public async Task<bool> ReconcileTerminalPayoutAsync(
        PayoutBatchPreview preview,
        PayoutBatchResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedItems = PayoutContractValidation.NormalizeTerminalResult(preview, result);
        var resultSha = PayoutResultSha(result, normalizedItems);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var sqliteTransaction = (SqliteTransaction)transaction;
            var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = """
                SELECT preview_sha256, status, provider_status, result_sha256, paid_cents
                FROM payout_batches WHERE batch_id = $batch;
                """;
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The terminal payout batch does not exist.");
            }

            var previewSha = reader.GetString(0);
            var status = reader.GetString(1);
            var providerStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
            var existingResultSha = reader.IsDBNull(3) ? null : reader.GetString(3);
            var paidCents = reader.GetInt64(4);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!string.Equals(previewSha, PayoutPreviewSha(preview), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The terminal payout preview failed its stored digest.");
            }

            if (status is not ("succeeded" or "partially_succeeded") ||
                string.IsNullOrWhiteSpace(providerStatus) || string.IsNullOrWhiteSpace(existingResultSha))
            {
                throw new InvalidOperationException("The payout batch is not eligible for terminal reconciliation.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                SELECT contributor_pseudonym, amount_cents, recipient_snapshot_id,
                       destination_sha256, succeeded, provider_status
                FROM payout_item_results
                WHERE batch_id = $batch
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            var storedItems = new List<DurablePayoutItemState>();
            await using (var itemReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    storedItems.Add(new DurablePayoutItemState(
                        preview.BatchId,
                        itemReader.GetString(0),
                        itemReader.GetInt64(1),
                        itemReader.GetString(2),
                        itemReader.GetString(3),
                        itemReader.GetInt32(4) == 1,
                        itemReader.GetString(5)));
                }
            }

            if (storedItems.Count != preview.Items.Count)
            {
                throw new InvalidOperationException("The terminal payout item state is incomplete.");
            }

            for (var index = 0; index < preview.Items.Count; index++)
            {
                var approved = preview.Items[index];
                var stored = storedItems[index];
                if (!string.Equals(stored.ContributorPseudonym, approved.ContributorPseudonym, StringComparison.Ordinal) ||
                    stored.AmountCents != approved.AmountCents ||
                    !string.Equals(stored.RecipientSnapshotId, approved.RecipientApproval.SnapshotId, StringComparison.Ordinal) ||
                    !string.Equals(stored.DestinationSha256, approved.RecipientApproval.DestinationSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The terminal payout item state conflicts with its approved preview.");
                }
            }

            var storedPaidCents = storedItems.Where(item => item.Succeeded).Sum(item => item.AmountCents);
            if (storedPaidCents != paidCents)
            {
                throw new InvalidOperationException("The terminal payout paid total conflicts with its item states.");
            }

            var storedOutcome = status == "succeeded"
                ? PayoutBatchOutcome.Succeeded
                : PayoutBatchOutcome.PartiallySucceeded;
            var storedResult = new PayoutBatchResult(
                preview.BatchId,
                storedOutcome,
                providerStatus,
                storedItems.Select(item => new PayoutItemResult(
                    item.ContributorPseudonym,
                    item.AmountCents,
                    item.Succeeded,
                    item.ProviderStatus)).ToArray());
            if (!string.Equals(
                    existingResultSha,
                    PayoutResultSha(storedResult, storedResult.ItemResults!),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The terminal payout result failed its stored digest.");
            }

            if (string.Equals(existingResultSha, resultSha, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(providerStatus, result.ProviderStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The terminal payout provider status changed unexpectedly.");
            }

            var transitionedOrdinals = new List<int>();
            long returnedCents = 0;
            for (var index = 0; index < storedItems.Count; index++)
            {
                var stored = storedItems[index];
                var current = normalizedItems[index];
                if (!stored.Succeeded)
                {
                    if (current.Succeeded ||
                        !string.Equals(stored.ProviderStatus, current.ProviderStatus, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "A terminal payout reconciliation cannot promote or rewrite an unsuccessful item.");
                    }

                    continue;
                }

                if (current.Succeeded)
                {
                    if (!string.Equals(stored.ProviderStatus, current.ProviderStatus, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "A terminal payout reconciliation cannot rewrite a successful item.");
                    }

                    continue;
                }

                if (!IsPayoutReturnStatus(current.ProviderStatus))
                {
                    throw new InvalidOperationException(
                        "Only a provider return, refund or reversal may correct a successful payout item.");
                }

                returnedCents = checked(returnedCents + stored.AmountCents);
                transitionedOrdinals.Add(index);
            }

            if (transitionedOrdinals.Count == 0 || returnedCents <= 0)
            {
                throw new InvalidOperationException("The terminal payout reconciliation has no valid correction.");
            }

            var remainingPaidCents = checked(paidCents - returnedCents);
            if (remainingPaidCents < 0)
            {
                throw new InvalidOperationException("The terminal payout correction exceeds its paid total.");
            }

            var targetStatus = remainingPaidCents == 0 ? "failed" : "partially_succeeded";
            var ledgerEventId = $"payout_return:{resultSha}";
            foreach (var ordinal in transitionedOrdinals)
            {
                var stored = storedItems[ordinal];
                var current = normalizedItems[ordinal];
                command.Parameters.Clear();
                command.CommandText = """
                    UPDATE payout_item_results
                    SET succeeded = 0, provider_status = $to_status
                    WHERE batch_id = $batch AND ordinal = $ordinal
                      AND succeeded = 1 AND provider_status = $from_status;
                    """;
                command.Parameters.AddWithValue("$to_status", current.ProviderStatus);
                command.Parameters.AddWithValue("$batch", preview.BatchId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$from_status", stored.ProviderStatus);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The terminal payout item correction conflicted.");
                }

                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO payout_item_adjustments(
                        batch_id, ordinal, from_provider_status, to_provider_status,
                        amount_cents, ledger_event_id, occurred_utc)
                    VALUES($batch, $ordinal, $from_status, $to_status,
                        $amount, $ledger_event, $occurred);
                    """;
                command.Parameters.AddWithValue("$batch", preview.BatchId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$from_status", stored.ProviderStatus);
                command.Parameters.AddWithValue("$to_status", current.ProviderStatus);
                command.Parameters.AddWithValue("$amount", stored.AmountCents);
                command.Parameters.AddWithValue("$ledger_event", ledgerEventId);
                command.Parameters.AddWithValue("$occurred", now.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE payout_batches
                SET status = $status, result_sha256 = $result_sha,
                    paid_cents = $paid, updated_utc = $updated
                WHERE batch_id = $batch AND status = $prior_status
                  AND result_sha256 = $prior_result_sha AND paid_cents = $prior_paid;
                """;
            command.Parameters.AddWithValue("$status", targetStatus);
            command.Parameters.AddWithValue("$result_sha", resultSha);
            command.Parameters.AddWithValue("$paid", remainingPaidCents);
            command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            command.Parameters.AddWithValue("$prior_status", status);
            command.Parameters.AddWithValue("$prior_result_sha", existingResultSha);
            command.Parameters.AddWithValue("$prior_paid", paidCents);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The terminal payout correction conflicted.");
            }

            await PostInternalAsync(
                    connection,
                    sqliteTransaction,
                    CareLedgerJournal.SandboxPayoutReturn(ledgerEventId, returnedCents, now),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<long> BalanceAsync(
        LedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(debit_cents - credit_cents), 0) FROM ledger_postings WHERE account = $account;";
        command.Parameters.AddWithValue("$account", (int)account);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> ReconcilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT event_id FROM ledger_postings
                GROUP BY event_id HAVING SUM(debit_cents) <> SUM(credit_cents)
            );
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0;
    }

    public async Task<SubscriptionEntitlement?> GetEntitlementAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT active, updated_utc FROM entitlements WHERE subscription_id = $id;";
        command.Parameters.AddWithValue("$id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubscriptionEntitlement(subscriptionId, reader.GetInt32(0) == 1, DateTimeOffset.Parse(reader.GetString(1)))
            : null;
    }

    public async Task<DurablePayoutState?> GetPayoutStateAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT status, provider_status, updated_utc, paid_cents FROM payout_batches WHERE batch_id = $batch;";
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DurablePayoutState(
                batchId,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetInt64(3))
            : null;
    }

    public async Task<IReadOnlyList<DurablePayoutItemState>> GetPayoutItemStatesAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        PayPalSandboxValidation.RequireLocalBatchId(batchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT contributor_pseudonym, amount_cents, recipient_snapshot_id,
                   destination_sha256, succeeded, provider_status
            FROM payout_item_results
            WHERE batch_id = $batch
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        var results = new List<DurablePayoutItemState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DurablePayoutItemState(
                batchId,
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetString(5)));
        }

        return results;
    }

    public async Task<IReadOnlyList<DurablePayoutItemAdjustment>> GetPayoutItemAdjustmentsAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        PayPalSandboxValidation.RequireLocalBatchId(batchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT adjustment.ordinal, item.contributor_pseudonym, adjustment.amount_cents,
                   adjustment.from_provider_status, adjustment.to_provider_status,
                   adjustment.ledger_event_id, adjustment.occurred_utc
            FROM payout_item_adjustments AS adjustment
            INNER JOIN payout_item_results AS item
              ON item.batch_id = adjustment.batch_id AND item.ordinal = adjustment.ordinal
            WHERE adjustment.batch_id = $batch
            ORDER BY adjustment.ordinal;
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        var results = new List<DurablePayoutItemAdjustment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DurablePayoutItemAdjustment(
                batchId,
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return results;
    }

    public async Task<string?> GetProviderBatchIdAsync(
        string localBatchId,
        CancellationToken cancellationToken = default)
    {
        PayPalSandboxValidation.RequireLocalBatchId(localBatchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_batch_id FROM payout_batches WHERE batch_id = $batch;";
        command.Parameters.AddWithValue("$batch", localBatchId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task StoreProviderBatchIdAsync(
        string localBatchId,
        string providerBatchId,
        CancellationToken cancellationToken = default)
    {
        PayPalSandboxValidation.RequireLocalBatchId(localBatchId);
        PayPalSandboxValidation.RequireProviderBatchId(providerBatchId);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT status, provider_batch_id FROM payout_batches WHERE batch_id = $batch;";
            command.Parameters.AddWithValue("$batch", localBatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The provider payout reference has no reserved local batch.");
            }

            var status = reader.GetString(0);
            var existing = reader.IsDBNull(1) ? null : reader.GetString(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (status != "reserved")
            {
                throw new InvalidOperationException("The provider payout reference requires a reserved local batch.");
            }

            if (existing is not null)
            {
                if (!string.Equals(existing, providerBatchId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The provider payout reference conflicts with prior data.");
                }

                return;
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE payout_batches SET provider_batch_id = $provider
                WHERE batch_id = $batch AND provider_batch_id IS NULL;
                """;
            command.Parameters.AddWithValue("$provider", providerBatchId);
            command.Parameters.AddWithValue("$batch", localBatchId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The provider payout reference transition conflicted.");
            }
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<DurableWebhookAuditRecord?> GetWebhookAuditAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 128)
        {
            throw new ArgumentException("A bounded webhook event ID is required.", nameof(eventId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transmission_id, event_type, subscription_sha256, payload_sha256,
                   occurred_utc, decision_code
            FROM webhook_events WHERE event_id = $event;
            """;
        command.Parameters.AddWithValue("$event", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DurableWebhookAuditRecord(
                eventId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetString(5))
            : null;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (schemaVersion > 9)
        {
            throw new InvalidOperationException("The CARE ledger schema is newer than this application.");
        }

        var hadPayoutTable = await TableExistsAsync(
            connection, "payout_batches", cancellationToken).ConfigureAwait(false);

        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS ledger_entries(
                event_id TEXT PRIMARY KEY,
                occurred_utc TEXT NOT NULL,
                description_code TEXT NOT NULL,
                canonical_sha256 TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ledger_postings(
                event_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                account INTEGER NOT NULL,
                debit_cents INTEGER NOT NULL CHECK(debit_cents >= 0),
                credit_cents INTEGER NOT NULL CHECK(credit_cents >= 0),
                PRIMARY KEY(event_id, ordinal),
                FOREIGN KEY(event_id) REFERENCES ledger_entries(event_id)
            );
            CREATE TABLE IF NOT EXISTS webhook_events(
                event_id TEXT PRIMARY KEY,
                transmission_id TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL,
                subscription_sha256 TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                occurred_utc TEXT NOT NULL,
                decision_code TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entitlements(
                subscription_id TEXT PRIMARY KEY,
                active INTEGER NOT NULL CHECK(active IN (0, 1)),
                updated_utc TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                source_event_type TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS subscription_provider_bindings(
                subscription_id TEXT PRIMARY KEY,
                plan_id TEXT NOT NULL,
                source_event_id TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sale_provider_bindings(
                sale_id TEXT PRIMARY KEY,
                subscription_id TEXT NOT NULL,
                gross_cents INTEGER NOT NULL CHECK(gross_cents > 0),
                corrected_cents INTEGER NOT NULL DEFAULT 0
                    CHECK(corrected_cents >= 0 AND corrected_cents <= gross_cents),
                reversed INTEGER NOT NULL DEFAULT 0 CHECK(reversed IN (0, 1)),
                source_event_id TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(subscription_id)
                    REFERENCES subscription_provider_bindings(subscription_id)
            );
            CREATE TABLE IF NOT EXISTS correction_provider_objects(
                provider_object_id TEXT PRIMARY KEY,
                sale_id TEXT NOT NULL,
                amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
                event_type TEXT NOT NULL,
                source_event_id TEXT NOT NULL UNIQUE,
                FOREIGN KEY(sale_id) REFERENCES sale_provider_bindings(sale_id)
            );
            CREATE TABLE IF NOT EXISTS subscription_financial_totals(
                subscription_id TEXT PRIMARY KEY,
                received_cents INTEGER NOT NULL DEFAULT 0 CHECK(received_cents >= 0),
                corrected_cents INTEGER NOT NULL DEFAULT 0
                    CHECK(corrected_cents >= 0 AND corrected_cents <= received_cents)
            );
            CREATE TABLE IF NOT EXISTS payout_batches(
                batch_id TEXT PRIMARY KEY,
                preview_sha256 TEXT NOT NULL,
                preview_json TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN (
                    'reserved', 'succeeded', 'failed', 'partially_succeeded', 'review_required')),
                provider_status TEXT,
                provider_batch_id TEXT,
                result_sha256 TEXT,
                remediation_review_sha256 TEXT,
                paid_cents INTEGER NOT NULL DEFAULT 0 CHECK(paid_cents >= 0),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "webhook_events",
            "subscription_sha256",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "webhook_events",
            "decision_code",
            "TEXT NOT NULL DEFAULT 'legacy_unclassified'",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "entitlements",
            "source_event_id",
            "TEXT NOT NULL DEFAULT 'legacy:unknown'",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "entitlements",
            "source_event_type",
            "TEXT NOT NULL DEFAULT 'legacy_unknown'",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "payout_batches",
            "provider_batch_id",
            "TEXT",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "payout_batches",
            "result_sha256",
            "TEXT",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "payout_batches",
            "paid_cents",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken).ConfigureAwait(false);
        if (hadPayoutTable && schemaVersion < 4)
        {
            await MigratePayoutSchemaV4Async(connection, cancellationToken).ConfigureAwait(false);
        }
        await EnsureColumnAsync(
            connection,
            "payout_batches",
            "remediation_review_sha256",
            "TEXT",
            cancellationToken).ConfigureAwait(false);

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS payout_item_results(
                batch_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                contributor_pseudonym TEXT NOT NULL,
                amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
                recipient_snapshot_id TEXT NOT NULL,
                destination_sha256 TEXT NOT NULL,
                succeeded INTEGER NOT NULL CHECK(succeeded IN (0, 1)),
                provider_status TEXT NOT NULL,
                PRIMARY KEY(batch_id, ordinal),
                UNIQUE(batch_id, contributor_pseudonym),
                UNIQUE(batch_id, recipient_snapshot_id),
                FOREIGN KEY(batch_id) REFERENCES payout_batches(batch_id)
            );
            CREATE TABLE IF NOT EXISTS payout_item_adjustments(
                batch_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                from_provider_status TEXT NOT NULL,
                to_provider_status TEXT NOT NULL CHECK(to_provider_status IN (
                    'RETURNED', 'REFUNDED', 'REVERSED')),
                amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
                ledger_event_id TEXT NOT NULL,
                occurred_utc TEXT NOT NULL,
                PRIMARY KEY(batch_id, ordinal),
                FOREIGN KEY(batch_id, ordinal)
                    REFERENCES payout_item_results(batch_id, ordinal)
            );
            CREATE TABLE IF NOT EXISTS payout_item_remediations(
                source_batch_id TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL CHECK(source_ordinal >= 0),
                remediation_batch_id TEXT NOT NULL,
                remediation_ordinal INTEGER NOT NULL CHECK(remediation_ordinal >= 0),
                contributor_pseudonym TEXT NOT NULL,
                amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
                source_provider_status TEXT NOT NULL,
                replacement_recipient_snapshot_id TEXT NOT NULL,
                replacement_destination_sha256 TEXT NOT NULL,
                source_result_sha256 TEXT NOT NULL,
                review_sha256 TEXT NOT NULL,
                approved_utc TEXT NOT NULL,
                PRIMARY KEY(source_batch_id, source_ordinal),
                UNIQUE(remediation_batch_id, remediation_ordinal),
                FOREIGN KEY(source_batch_id, source_ordinal)
                    REFERENCES payout_item_results(batch_id, ordinal),
                FOREIGN KEY(remediation_batch_id)
                    REFERENCES payout_batches(batch_id)
            );
            CREATE TABLE IF NOT EXISTS webhook_event_quarantine(
                event_id TEXT PRIMARY KEY,
                transmission_id TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL CHECK(event_type IN (
                    'PAYMENT.SALE.COMPLETED',
                    'PAYMENT.SALE.REFUNDED',
                    'PAYMENT.SALE.REVERSED')),
                provider_object_id TEXT NOT NULL,
                subscription_id TEXT,
                plan_id TEXT,
                related_sale_id TEXT,
                amount_cents INTEGER NOT NULL CHECK(amount_cents BETWEEN 1 AND 300),
                currency TEXT NOT NULL CHECK(currency = 'USD'),
                occurred_utc TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64 AND
                    payload_sha256 = lower(payload_sha256) AND
                    payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                expected_plan_id TEXT NOT NULL,
                verified_event_sha256 TEXT NOT NULL CHECK(
                    length(verified_event_sha256) = 64 AND
                    verified_event_sha256 = lower(verified_event_sha256) AND
                    verified_event_sha256 NOT GLOB '*[^0-9a-f]*'),
                reason_code TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('pending', 'completed', 'blocked')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                first_received_utc TEXT NOT NULL,
                last_attempt_utc TEXT,
                next_attempt_utc TEXT,
                completed_utc TEXT,
                CHECK(
                    (state = 'pending' AND next_attempt_utc IS NOT NULL AND completed_utc IS NULL) OR
                    (state = 'completed' AND next_attempt_utc IS NULL AND completed_utc IS NOT NULL) OR
                    (state = 'blocked' AND next_attempt_utc IS NULL AND completed_utc IS NULL))
            );
            CREATE TABLE IF NOT EXISTS care_operator_action_audit(
                sequence INTEGER PRIMARY KEY CHECK(sequence > 0),
                occurred_utc TEXT NOT NULL,
                operator_pseudonym_sha256 TEXT NOT NULL CHECK(
                    length(operator_pseudonym_sha256) = 64 AND
                    operator_pseudonym_sha256 = lower(operator_pseudonym_sha256) AND
                    operator_pseudonym_sha256 NOT GLOB '*[^0-9a-f]*'),
                action_code TEXT NOT NULL CHECK(action_code = 'process_due'),
                decision_code TEXT NOT NULL CHECK(decision_code IN (
                    'approval_required', 'processing_disabled', 'review_stale',
                    'nothing_due', 'processed')),
                reviewed_health_sha256 TEXT NOT NULL CHECK(
                    length(reviewed_health_sha256) = 64 AND
                    reviewed_health_sha256 = lower(reviewed_health_sha256) AND
                    reviewed_health_sha256 NOT GLOB '*[^0-9a-f]*'),
                result_sha256 TEXT CHECK(
                    result_sha256 IS NULL OR (
                        length(result_sha256) = 64 AND
                        result_sha256 = lower(result_sha256) AND
                        result_sha256 NOT GLOB '*[^0-9a-f]*')),
                previous_record_sha256 TEXT NOT NULL CHECK(
                    length(previous_record_sha256) = 64 AND
                    previous_record_sha256 = lower(previous_record_sha256) AND
                    previous_record_sha256 NOT GLOB '*[^0-9a-f]*'),
                record_sha256 TEXT NOT NULL UNIQUE CHECK(
                    length(record_sha256) = 64 AND
                    record_sha256 = lower(record_sha256) AND
                    record_sha256 NOT GLOB '*[^0-9a-f]*'),
                CHECK((decision_code = 'processed') = (result_sha256 IS NOT NULL))
            );
            CREATE TRIGGER IF NOT EXISTS care_operator_action_audit_no_update
            BEFORE UPDATE ON care_operator_action_audit
            BEGIN
                SELECT RAISE(ABORT, 'CARE operator audit is append-only');
            END;
            CREATE TRIGGER IF NOT EXISTS care_operator_action_audit_no_delete
            BEFORE DELETE ON care_operator_action_audit
            BEGIN
                SELECT RAISE(ABORT, 'CARE operator audit is append-only');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_payout_batches_provider_batch_id
            ON payout_batches(provider_batch_id)
            WHERE provider_batch_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_webhook_event_quarantine_due
            ON webhook_event_quarantine(state, next_attempt_utc, occurred_utc, event_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "PRAGMA user_version = 9;";
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

    private static async Task<bool> PostInternalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateEntry(entry);
        var sha = EntrySha(entry);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_sha256 FROM ledger_entries WHERE event_id = $event;";
        command.Parameters.AddWithValue("$event", entry.EventId);
        var existing = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing, sha, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A durable ledger event ID conflicts with prior content.");
            }

            return false;
        }

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO ledger_entries(event_id, occurred_utc, description_code, canonical_sha256) VALUES($event, $occurred, $description, $sha);";
        command.Parameters.AddWithValue("$event", entry.EventId);
        command.Parameters.AddWithValue("$occurred", entry.OccurredUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$description", entry.DescriptionCode);
        command.Parameters.AddWithValue("$sha", sha);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < entry.Postings.Count; ordinal++)
        {
            var posting = entry.Postings[ordinal];
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO ledger_postings(event_id, ordinal, account, debit_cents, credit_cents) VALUES($event, $ordinal, $account, $debit, $credit);";
            command.Parameters.AddWithValue("$event", entry.EventId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$account", (int)posting.Account);
            command.Parameters.AddWithValue("$debit", posting.DebitCents);
            command.Parameters.AddWithValue("$credit", posting.CreditCents);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static void ValidateEntry(LedgerJournalEntry entry)
    {
        var ledger = new DoubleEntryLedger();
        ledger.Post(entry);
    }

    private static string EntrySha(LedgerJournalEntry entry) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry)));

    private static string PayoutPreviewSha(PayoutBatchPreview preview) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(preview)));

    private static string PayoutResultSha(
        PayoutBatchResult result,
        IReadOnlyList<PayoutItemResult> normalizedItems) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.BatchId,
            result.Outcome,
            result.ProviderStatus,
            Items = normalizedItems,
        })));

    private static bool IsPayoutReturnStatus(string status) =>
        status is "RETURNED" or "REFUNDED" or "REVERSED";

    private static void ValidatePayout(
        PayoutBatchPreview preview,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today)
    {
        PayoutContractValidation.RequirePreview(preview);
        if (!accountEligibilityVerified || !ownerApproved || today < preview.EligibilityDate)
        {
            throw new InvalidOperationException("The durable sandbox payout gate is not satisfied; live payouts are disabled.");
        }
    }

    private static void ValidateWebhook(PayPalSandboxWebhookEvent webhook)
        => PayPalSandboxWebhookPolicy.Validate(webhook);

    private static async Task<PayPalSandboxWebhookEvent> NormalizeOfficialWebhookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayPalSandboxVerifiedWebhook verified,
        string expectedPlanId,
        CancellationToken cancellationToken)
    {
        if (!PayoutContractValidation.IsLowerSha256(verified.PayloadSha256))
        {
            throw new PayPalSandboxException("paypal_sandbox_webhook_payload_digest_invalid");
        }

        string subscriptionId;
        if (verified.EventType.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal))
        {
            if (verified.SubscriptionId is null || verified.PlanId is null ||
                !string.Equals(verified.PlanId, expectedPlanId, StringComparison.Ordinal))
            {
                throw new PayPalSandboxException("paypal_sandbox_subscription_plan_mismatch");
            }

            subscriptionId = verified.SubscriptionId;
            await RequireOrCreateSubscriptionBindingAsync(
                    connection,
                    transaction,
                    subscriptionId,
                    expectedPlanId,
                    verified.EventId,
                    verified.OccurredUtc,
                    permitCreate: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (verified.EventType == "PAYMENT.SALE.COMPLETED")
        {
            if (verified.SubscriptionId is null || verified.RelatedSaleId is null ||
                !string.Equals(
                    verified.ProviderObjectId,
                    verified.RelatedSaleId,
                    StringComparison.Ordinal))
            {
                throw new PayPalSandboxException("paypal_sandbox_subscription_mapping_invalid");
            }

            subscriptionId = verified.SubscriptionId;
            await RequireOrCreateSubscriptionBindingAsync(
                    connection,
                    transaction,
                    subscriptionId,
                    expectedPlanId,
                    verified.EventId,
                    verified.OccurredUtc,
                    permitCreate: false,
                    cancellationToken)
                .ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sale_provider_bindings(
                    sale_id, subscription_id, gross_cents, corrected_cents,
                    reversed, source_event_id, created_utc)
                VALUES($sale, $subscription, $gross, 0, 0, $event, $created);
                """;
            command.Parameters.AddWithValue("$sale", verified.RelatedSaleId);
            command.Parameters.AddWithValue("$subscription", subscriptionId);
            command.Parameters.AddWithValue("$gross", verified.AmountCents);
            command.Parameters.AddWithValue("$event", verified.EventId);
            command.Parameters.AddWithValue("$created", verified.OccurredUtc.ToUniversalTime().ToString("O"));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new PayPalSandboxException("paypal_sandbox_sale_mapping_conflict");
            }
        }
        else
        {
            if (verified.RelatedSaleId is null)
            {
                throw new PayPalSandboxException("paypal_sandbox_sale_mapping_missing");
            }

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subscription_id, gross_cents, corrected_cents, reversed
                FROM sale_provider_bindings
                WHERE sale_id = $sale;
                """;
            command.Parameters.AddWithValue("$sale", verified.RelatedSaleId);
            long grossCents;
            long correctedCents;
            bool reversed;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new PayPalSandboxException("paypal_sandbox_sale_mapping_missing");
                }

                subscriptionId = reader.GetString(0);
                grossCents = reader.GetInt64(1);
                correctedCents = reader.GetInt64(2);
                reversed = reader.GetInt32(3) == 1;
            }

            if ((verified.SubscriptionId is not null &&
                 !string.Equals(verified.SubscriptionId, subscriptionId, StringComparison.Ordinal)) ||
                reversed || verified.AmountCents > grossCents - correctedCents)
            {
                throw new PayPalSandboxException("paypal_sandbox_correction_invalid");
            }

            await RequireOrCreateSubscriptionBindingAsync(
                    connection,
                    transaction,
                    subscriptionId,
                    expectedPlanId,
                    verified.EventId,
                    verified.OccurredUtc,
                    permitCreate: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(
                    verified.ProviderObjectId,
                    verified.RelatedSaleId,
                    StringComparison.Ordinal))
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO correction_provider_objects(
                        provider_object_id, sale_id, amount_cents, event_type, source_event_id)
                    VALUES($object, $sale, $amount, $type, $event);
                    """;
                command.Parameters.AddWithValue("$object", verified.ProviderObjectId);
                command.Parameters.AddWithValue("$sale", verified.RelatedSaleId);
                command.Parameters.AddWithValue("$amount", verified.AmountCents);
                command.Parameters.AddWithValue("$type", verified.EventType);
                command.Parameters.AddWithValue("$event", verified.EventId);
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    throw new PayPalSandboxException("paypal_sandbox_correction_object_conflict");
                }
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE sale_provider_bindings
                SET corrected_cents = corrected_cents + $amount,
                    reversed = CASE WHEN $is_reversal = 1 THEN 1 ELSE reversed END
                WHERE sale_id = $sale
                  AND corrected_cents = $prior_corrected
                  AND reversed = 0;
                """;
            command.Parameters.AddWithValue("$amount", verified.AmountCents);
            command.Parameters.AddWithValue(
                "$is_reversal",
                verified.EventType == "PAYMENT.SALE.REVERSED" ? 1 : 0);
            command.Parameters.AddWithValue("$sale", verified.RelatedSaleId);
            command.Parameters.AddWithValue("$prior_corrected", correctedCents);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new PayPalSandboxException("paypal_sandbox_correction_conflict");
            }
        }

        var webhook = new PayPalSandboxWebhookEvent(
            verified.EventId,
            verified.TransmissionId,
            verified.EventType,
            subscriptionId,
            verified.AmountCents,
            verified.Currency,
            verified.OccurredUtc);
        ValidateWebhook(webhook);
        return webhook;
    }

    private static async Task RequireOrCreateSubscriptionBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subscriptionId,
        string expectedPlanId,
        string eventId,
        DateTimeOffset occurredUtc,
        bool permitCreate,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT plan_id
            FROM subscription_provider_bindings
            WHERE subscription_id = $subscription;
            """;
        command.Parameters.AddWithValue("$subscription", subscriptionId);
        var existingPlanId = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existingPlanId is not null)
        {
            if (!string.Equals(existingPlanId, expectedPlanId, StringComparison.Ordinal))
            {
                throw new PayPalSandboxException("paypal_sandbox_subscription_plan_mismatch");
            }

            return;
        }

        if (!permitCreate)
        {
            throw new PayPalSandboxException("paypal_sandbox_subscription_mapping_missing");
        }

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO subscription_provider_bindings(
                subscription_id, plan_id, source_event_id, created_utc)
            VALUES($subscription, $plan, $event, $created);
            """;
        command.Parameters.AddWithValue("$subscription", subscriptionId);
        command.Parameters.AddWithValue("$plan", expectedPlanId);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$created", occurredUtc.ToUniversalTime().ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new PayPalSandboxException("paypal_sandbox_subscription_mapping_conflict");
        }
    }

    private static LedgerJournalEntry? CreateWebhookEntry(PayPalSandboxWebhookEvent webhook) => webhook.EventType switch
    {
        "PAYMENT.SALE.COMPLETED" => new LedgerJournalEntry(
            webhook.EventId,
            webhook.OccurredUtc,
            "subscription_payment",
            [
                new LedgerPosting(LedgerAccount.Cash, webhook.AmountCents, 0),
                new LedgerPosting(LedgerAccount.SubscriptionRevenue, 0, webhook.AmountCents),
            ]),
        "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED" => new LedgerJournalEntry(
            webhook.EventId,
            webhook.OccurredUtc,
            "refund_or_chargeback",
            [
                new LedgerPosting(LedgerAccount.RefundsAndChargebacks, webhook.AmountCents, 0),
                new LedgerPosting(LedgerAccount.Cash, 0, webhook.AmountCents),
            ]),
        _ => null,
    };

    private static async Task ApplySubscriptionFinancialInvariantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayPalSandboxWebhookEvent webhook,
        CancellationToken cancellationToken)
    {
        if (webhook.EventType is not ("PAYMENT.SALE.COMPLETED" or
            "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED"))
        {
            return;
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT received_cents, corrected_cents
            FROM subscription_financial_totals
            WHERE subscription_id = $subscription;
            """;
        command.Parameters.AddWithValue("$subscription", webhook.SubscriptionId);
        long receivedCents = 0;
        long correctedCents = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                receivedCents = reader.GetInt64(0);
                correctedCents = reader.GetInt64(1);
            }
        }

        if (webhook.EventType == "PAYMENT.SALE.COMPLETED")
        {
            receivedCents = checked(receivedCents + webhook.AmountCents);
        }
        else
        {
            if (webhook.AmountCents > receivedCents - correctedCents)
            {
                throw new InvalidOperationException(
                    "The sandbox correction exceeds recorded subscription payments.");
            }

            correctedCents = checked(correctedCents + webhook.AmountCents);
        }

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO subscription_financial_totals(
                subscription_id, received_cents, corrected_cents)
            VALUES($subscription, $received, $corrected)
            ON CONFLICT(subscription_id) DO UPDATE SET
                received_cents = excluded.received_cents,
                corrected_cents = excluded.corrected_cents;
            """;
        command.Parameters.AddWithValue("$subscription", webhook.SubscriptionId);
        command.Parameters.AddWithValue("$received", receivedCents);
        command.Parameters.AddWithValue("$corrected", correctedCents);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WebhookRow?> ReadWebhookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string transmissionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_id, transmission_id, payload_sha256 FROM webhook_events WHERE event_id = $event OR transmission_id = $transmission;";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$transmission", transmissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WebhookRow(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private sealed record WebhookRow(string EventId, string TransmissionId, string PayloadSha256);

    private static async Task<SubscriptionEntitlement?> ReadEntitlementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT active, updated_utc FROM entitlements WHERE subscription_id = $id;";
        command.Parameters.AddWithValue("$id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubscriptionEntitlement(
                subscriptionId,
                reader.GetInt32(0) == 1,
                DateTimeOffset.Parse(reader.GetString(1)))
            : null;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string declaration,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task MigratePayoutSchemaV4Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS ux_payout_batches_provider_batch_id;
            ALTER TABLE payout_batches RENAME TO payout_batches_v3;
            CREATE TABLE payout_batches(
                batch_id TEXT PRIMARY KEY,
                preview_sha256 TEXT NOT NULL,
                preview_json TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN (
                    'reserved', 'succeeded', 'failed', 'partially_succeeded', 'review_required')),
                provider_status TEXT,
                provider_batch_id TEXT,
                result_sha256 TEXT,
                remediation_review_sha256 TEXT,
                paid_cents INTEGER NOT NULL DEFAULT 0 CHECK(paid_cents >= 0),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            INSERT INTO payout_batches(
                batch_id, preview_sha256, preview_json, status, provider_status,
                provider_batch_id, result_sha256, paid_cents, created_utc, updated_utc)
            SELECT batch_id, preview_sha256, preview_json,
                   CASE WHEN status = 'reserved' THEN 'review_required' ELSE status END,
                   CASE WHEN status = 'reserved'
                        THEN 'RECIPIENT_REAPPROVAL_REQUIRED'
                        ELSE provider_status END,
                   provider_batch_id, result_sha256, paid_cents, created_utc, updated_utc
            FROM payout_batches_v3;
            DROP TABLE payout_batches_v3;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed partial class DurablePayoutCoordinator(
    DurableCareRepository repository,
    IPayoutSandboxProvider provider)
{
    public async Task<PayoutBatchResult> ExecuteAsync(
        PayoutBatchPreview preview,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await repository.ReservePayoutAsync(
                preview,
                accountEligibilityVerified,
                ownerApproved,
                today,
                now,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The payout batch was already durably reserved or executed.");
        }

        var result = await provider.SendSandboxAsync(preview, cancellationToken).ConfigureAwait(false);
        PayoutContractValidation.ValidateProviderResult(preview, result);
        if (result.IsTerminal)
        {
            await repository.CompletePayoutAsync(preview, result, now, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<PayoutReconciliationSummary> ReconcilePendingAsync(
        DateTimeOffset updatedBeforeOrAt,
        DateTimeOffset now,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (provider is not IReconcilablePayoutSandboxProvider reconcilable)
        {
            throw new InvalidOperationException("The sandbox payout provider does not support restart reconciliation.");
        }

        var pending = await repository.GetPendingPayoutsAsync(updatedBeforeOrAt, limit, cancellationToken)
            .ConfigureAwait(false);
        var completed = 0;
        var stillPending = 0;
        var resubmitted = 0;
        foreach (var item in pending)
        {
            var lookup = await reconcilable.LookupSandboxAsync(item.Preview, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(lookup.BatchId, item.Preview.BatchId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The sandbox payout lookup returned a conflicting batch identifier.");
            }

            PayoutBatchResult? result = null;
            switch (lookup.State)
            {
                case SandboxPayoutLookupState.NotFound:
                    result = await reconcilable.SendSandboxAsync(item.Preview, cancellationToken).ConfigureAwait(false);
                    PayoutContractValidation.ValidateProviderResult(item.Preview, result);
                    resubmitted++;
                    break;
                case SandboxPayoutLookupState.Pending:
                    stillPending++;
                    break;
                case SandboxPayoutLookupState.Succeeded:
                    result = new PayoutBatchResult(
                        item.Preview.BatchId,
                        PayoutBatchOutcome.Succeeded,
                        ValidateLookupStatus(lookup.ProviderStatus),
                        lookup.ItemResults);
                    break;
                case SandboxPayoutLookupState.Failed:
                    result = new PayoutBatchResult(
                        item.Preview.BatchId,
                        PayoutBatchOutcome.Failed,
                        ValidateLookupStatus(lookup.ProviderStatus),
                        lookup.ItemResults);
                    break;
                case SandboxPayoutLookupState.PartiallySucceeded:
                    result = new PayoutBatchResult(
                        item.Preview.BatchId,
                        PayoutBatchOutcome.PartiallySucceeded,
                        ValidateLookupStatus(lookup.ProviderStatus),
                        lookup.ItemResults);
                    break;
                default:
                    throw new InvalidOperationException("The sandbox payout lookup state is invalid.");
            }

            if (result is { IsTerminal: true })
            {
                await repository.CompletePayoutAsync(item.Preview, result, now, cancellationToken)
                    .ConfigureAwait(false);
                completed++;
            }
            else if (result is { IsTerminal: false })
            {
                stillPending++;
            }
        }

        return new PayoutReconciliationSummary(pending.Count, completed, stillPending, resubmitted);
    }

    public async Task<PayoutTerminalReconciliationSummary> ReconcileTerminalAsync(
        DateTimeOffset updatedBeforeOrAt,
        DateTimeOffset now,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (provider is not IReconcilablePayoutSandboxProvider reconcilable)
        {
            throw new InvalidOperationException("The sandbox payout provider does not support terminal reconciliation.");
        }

        var terminal = await repository.GetTerminalPayoutsAsync(updatedBeforeOrAt, limit, cancellationToken)
            .ConfigureAwait(false);
        var corrected = 0;
        var unchanged = 0;
        var providerPending = 0;
        foreach (var item in terminal)
        {
            var lookup = await reconcilable.LookupSandboxAsync(item.Preview, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(lookup.BatchId, item.Preview.BatchId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The sandbox payout lookup returned a conflicting batch identifier.");
            }

            PayoutBatchResult? result = lookup.State switch
            {
                SandboxPayoutLookupState.NotFound => throw new InvalidOperationException(
                    "A completed sandbox payout disappeared from the provider; automatic resend is prohibited."),
                SandboxPayoutLookupState.Pending => null,
                SandboxPayoutLookupState.Succeeded => new PayoutBatchResult(
                    item.Preview.BatchId,
                    PayoutBatchOutcome.Succeeded,
                    ValidateLookupStatus(lookup.ProviderStatus),
                    lookup.ItemResults),
                SandboxPayoutLookupState.Failed => new PayoutBatchResult(
                    item.Preview.BatchId,
                    PayoutBatchOutcome.Failed,
                    ValidateLookupStatus(lookup.ProviderStatus),
                    lookup.ItemResults),
                SandboxPayoutLookupState.PartiallySucceeded => new PayoutBatchResult(
                    item.Preview.BatchId,
                    PayoutBatchOutcome.PartiallySucceeded,
                    ValidateLookupStatus(lookup.ProviderStatus),
                    lookup.ItemResults),
                _ => throw new InvalidOperationException("The sandbox payout lookup state is invalid."),
            };

            if (result is null)
            {
                providerPending++;
                continue;
            }

            if (await repository.ReconcileTerminalPayoutAsync(
                    item.Preview, result, now, cancellationToken).ConfigureAwait(false))
            {
                corrected++;
            }
            else
            {
                unchanged++;
            }
        }

        return new PayoutTerminalReconciliationSummary(
            terminal.Count, corrected, unchanged, providerPending);
    }

    private static string ValidateLookupStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || status.Length > 128 || status.Any(char.IsControl))
        {
            throw new InvalidOperationException("The sandbox payout lookup returned an invalid provider status.");
        }

        return status;
    }

}
