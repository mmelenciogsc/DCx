using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record DurablePayoutState(
    string BatchId,
    string Status,
    string? ProviderStatus,
    DateTimeOffset UpdatedUtc);

public sealed class DurableCareRepository
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
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadWebhookAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    webhook.EventId,
                    webhook.TransmissionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.EventId == webhook.EventId && existing.TransmissionId == webhook.TransmissionId &&
                    string.Equals(existing.PayloadSha256, payloadSha, StringComparison.Ordinal))
                {
                    return false;
                }

                throw new InvalidOperationException("A webhook event or transmission identifier conflicts with prior data.");
            }

            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO webhook_events(event_id, transmission_id, event_type, payload_sha256, occurred_utc)
                VALUES($event, $transmission, $type, $sha, $occurred);
                """;
            command.Parameters.AddWithValue("$event", webhook.EventId);
            command.Parameters.AddWithValue("$transmission", webhook.TransmissionId);
            command.Parameters.AddWithValue("$type", webhook.EventType);
            command.Parameters.AddWithValue("$sha", payloadSha);
            command.Parameters.AddWithValue("$occurred", webhook.OccurredUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var entitlementActive = webhook.EventType == "BILLING.SUBSCRIPTION.ACTIVATED";
            if (entitlementActive || webhook.EventType is "BILLING.SUBSCRIPTION.CANCELLED" or
                "BILLING.SUBSCRIPTION.SUSPENDED" or "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED")
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO entitlements(subscription_id, active, updated_utc)
                    VALUES($subscription, $active, $updated)
                    ON CONFLICT(subscription_id) DO UPDATE SET active = excluded.active, updated_utc = excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("$subscription", webhook.SubscriptionId);
                command.Parameters.AddWithValue("$active", entitlementActive ? 1 : 0);
                command.Parameters.AddWithValue("$updated", webhook.OccurredUtc.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var journalEntry = CreateWebhookEntry(webhook);
            if (journalEntry is not null)
            {
                await PostInternalAsync(connection, (SqliteTransaction)transaction, journalEntry, cancellationToken)
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
        if (preview.BatchId != result.BatchId || preview.Mode != PayoutExecutionMode.Sandbox)
        {
            throw new InvalidOperationException("The payout result does not match a sandbox preview.");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT preview_sha256, status, provider_status FROM payout_batches WHERE batch_id = $batch;";
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The payout batch was not reserved.");
            }

            var sha = reader.GetString(0);
            var status = reader.GetString(1);
            var providerStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!string.Equals(sha, PayoutPreviewSha(preview), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The reserved payout preview was modified.");
            }

            var targetStatus = result.Succeeded ? "succeeded" : "failed";
            if (status != "reserved")
            {
                if (status == targetStatus && providerStatus == result.ProviderStatus)
                {
                    return false;
                }

                throw new InvalidOperationException("The payout batch has a conflicting terminal state.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE payout_batches
                SET status = $status, provider_status = $provider, updated_utc = $updated
                WHERE batch_id = $batch AND status = 'reserved';
                """;
            command.Parameters.AddWithValue("$status", targetStatus);
            command.Parameters.AddWithValue("$provider", result.ProviderStatus);
            command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The payout terminal transition conflicted.");
            }

            if (result.Succeeded)
            {
                await PostInternalAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        CareLedgerJournal.SandboxPayout($"payout:{preview.BatchId}", preview, now),
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
        command.CommandText = "SELECT status, provider_status, updated_utc FROM payout_batches WHERE batch_id = $batch;";
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DurablePayoutState(
                batchId,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
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
                payload_sha256 TEXT NOT NULL,
                occurred_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS entitlements(
                subscription_id TEXT PRIMARY KEY,
                active INTEGER NOT NULL CHECK(active IN (0, 1)),
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS payout_batches(
                batch_id TEXT PRIMARY KEY,
                preview_sha256 TEXT NOT NULL,
                preview_json TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('reserved', 'succeeded', 'failed')),
                provider_status TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
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

    private static void ValidatePayout(
        PayoutBatchPreview preview,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today)
    {
        if (preview.Mode != PayoutExecutionMode.Sandbox || !accountEligibilityVerified || !ownerApproved ||
            today < preview.EligibilityDate || string.IsNullOrWhiteSpace(preview.BatchId) || preview.Items.Count == 0 ||
            preview.Items.Any(item => item.AmountCents <= 0) ||
            preview.Items.Select(item => item.ContributorPseudonym).Distinct(StringComparer.Ordinal).Count() != preview.Items.Count)
        {
            throw new InvalidOperationException("The durable sandbox payout gate is not satisfied; live payouts are disabled.");
        }
    }

    private static void ValidateWebhook(PayPalSandboxWebhookEvent webhook)
    {
        if (!string.Equals(webhook.Currency, "USD", StringComparison.Ordinal) || webhook.AmountCents < 0 ||
            string.IsNullOrWhiteSpace(webhook.EventId) || string.IsNullOrWhiteSpace(webhook.TransmissionId) ||
            string.IsNullOrWhiteSpace(webhook.SubscriptionId) ||
            webhook.EventType is not ("BILLING.SUBSCRIPTION.ACTIVATED" or "BILLING.SUBSCRIPTION.CANCELLED" or
                "BILLING.SUBSCRIPTION.SUSPENDED" or "PAYMENT.SALE.COMPLETED" or
                "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED") ||
            webhook.EventType == "PAYMENT.SALE.COMPLETED" && webhook.AmountCents != PayPalSandboxWebhookProcessor.MonthlyPlanCents)
        {
            throw new InvalidOperationException("The sandbox webhook schema, event type, currency, or amount is invalid.");
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
}

public sealed class DurablePayoutCoordinator(
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
        await repository.CompletePayoutAsync(preview, result, now, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
