using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record DurableWebhookQuarantineRecord(
    string EventId,
    string TransmissionId,
    string EventType,
    string ProviderObjectId,
    string? SubscriptionId,
    string? PlanId,
    string? RelatedSaleId,
    long AmountCents,
    string Currency,
    DateTimeOffset OccurredUtc,
    string PayloadSha256,
    string ExpectedPlanId,
    string VerifiedEventSha256,
    string ReasonCode,
    string State,
    int AttemptCount,
    DateTimeOffset FirstReceivedUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? CompletedUtc);

public sealed record WebhookQuarantineReprocessSummary(
    int Scanned,
    int Completed,
    int StillPending,
    int Blocked);

public sealed record WebhookQuarantineHealthSnapshot(
    string StatusCode,
    int Pending,
    int Due,
    int Blocked,
    int Completed,
    int MaximumAttemptCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset? NextAttemptUtc);

public sealed partial class DurableCareRepository
{
    public async Task<WebhookQuarantineHealthSnapshot>
        GetOfficialWebhookQuarantineHealthAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
    {
        var nowUtc = now.ToUniversalTime();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN state = 'pending' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = 'pending' AND next_attempt_utc <= $now
                    THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = 'blocked' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = 'completed' THEN 1 ELSE 0 END), 0),
                COALESCE(MAX(attempt_count), 0),
                MIN(CASE WHEN state = 'pending' THEN first_received_utc END),
                MIN(CASE WHEN state = 'pending' THEN next_attempt_utc END)
            FROM webhook_event_quarantine;
            """;
        command.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The verified-event quarantine health query returned no aggregate row.");
        }

        var pending = checked((int)reader.GetInt64(0));
        var due = checked((int)reader.GetInt64(1));
        var blocked = checked((int)reader.GetInt64(2));
        var completed = checked((int)reader.GetInt64(3));
        var maximumAttempts = checked((int)reader.GetInt64(4));
        var oldestPendingUtc = reader.IsDBNull(5)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(5)).ToUniversalTime();
        var nextAttemptUtc = reader.IsDBNull(6)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(6)).ToUniversalTime();
        var statusCode = blocked > 0
            ? "operator_action_required"
            : due > 0
                ? "retry_due"
                : pending > 0
                    ? "retry_scheduled"
                    : "clear";
        return new WebhookQuarantineHealthSnapshot(
            statusCode,
            pending,
            due,
            blocked,
            completed,
            maximumAttempts,
            oldestPendingUtc,
            nextAttemptUtc);
    }

    public async Task<DurableWebhookQuarantineRecord?> GetOfficialWebhookQuarantineAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        RequireWebhookQuarantineIdentifier(eventId, 128, "event");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await ReadWebhookQuarantineAsync(
                connection, null, eventId, cancellationToken)
            .ConfigureAwait(false);
        if (record is not null) ValidateWebhookQuarantineRecord(record);
        return record;
    }

    public async Task<WebhookQuarantineReprocessSummary>
        ReprocessQuarantinedOfficialWebhooksAsync(
            DateTimeOffset now,
            int limit = 100,
            CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var nowUtc = now.ToUniversalTime();
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id
                FROM webhook_event_quarantine
                WHERE state = 'pending' AND next_attempt_utc <= $now
                ORDER BY
                    CASE event_type WHEN 'PAYMENT.SALE.COMPLETED' THEN 0 ELSE 1 END,
                    occurred_utc,
                    event_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            var eventIds = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    eventIds.Add(reader.GetString(0));
                }
            }

            var completed = 0;
            var stillPending = 0;
            var blocked = 0;
            foreach (var eventId in eventIds)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
                var sqliteTransaction = (SqliteTransaction)transaction;
                var record = await ReadWebhookQuarantineAsync(
                        connection, sqliteTransaction, eventId, cancellationToken)
                    .ConfigureAwait(false);
                if (record is null || record.State != "pending" ||
                    record.NextAttemptUtc is null || record.NextAttemptUtc > nowUtc)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ValidateWebhookQuarantineRecord(record);
                var existing = await ReadWebhookAsync(
                        connection,
                        sqliteTransaction,
                        record.EventId,
                        record.TransmissionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.EventId == record.EventId &&
                        existing.TransmissionId == record.TransmissionId &&
                        string.Equals(existing.PayloadSha256, record.PayloadSha256, StringComparison.Ordinal))
                    {
                        await MarkWebhookQuarantineCompletedAsync(
                                connection, sqliteTransaction, record, nowUtc, cancellationToken)
                            .ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        completed++;
                        continue;
                    }

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    await MarkWebhookQuarantineFailureAsync(
                            connection,
                            record,
                            "paypal_sandbox_quarantine_identity_conflict",
                            keepPending: false,
                            nowUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    blocked++;
                    continue;
                }

                var verified = ToVerifiedWebhook(record);
                PayPalSandboxWebhookEvent? normalized = null;
                string? failureCode = null;
                try
                {
                    normalized = await NormalizeOfficialWebhookAsync(
                            connection,
                            sqliteTransaction,
                            verified,
                            record.ExpectedPlanId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (PayPalSandboxException exception)
                {
                    failureCode = exception.Code;
                }

                if (failureCode is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    var keepPending = IsQuarantinableWebhookDependency(failureCode);
                    await MarkWebhookQuarantineFailureAsync(
                            connection,
                            record,
                            keepPending
                                ? failureCode
                                : "paypal_sandbox_quarantine_reprocess_blocked",
                            keepPending,
                            nowUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (keepPending) stillPending++;
                    else blocked++;
                    continue;
                }

                await ApplyNormalizedWebhookAsync(
                        connection,
                        sqliteTransaction,
                        normalized!,
                        record.PayloadSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                await MarkWebhookQuarantineCompletedAsync(
                        connection, sqliteTransaction, record, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                completed++;
            }

            return new WebhookQuarantineReprocessSummary(
                eventIds.Count, completed, stillPending, blocked);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private static async Task ApplyNormalizedWebhookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayPalSandboxWebhookEvent webhook,
        string payloadSha,
        CancellationToken cancellationToken)
    {
        ValidateWebhook(webhook);
        SubscriptionEntitlementTransition? entitlementTransition = null;
        var decisionCode = PayPalSandboxWebhookPolicy.LedgerOnly;
        if (PayPalSandboxWebhookPolicy.TryGetEntitlementTarget(
                webhook.EventType, out var targetActive))
        {
            var current = await ReadEntitlementAsync(
                    connection,
                    transaction,
                    webhook.SubscriptionId,
                    cancellationToken)
                .ConfigureAwait(false);
            entitlementTransition = PayPalSandboxWebhookPolicy.ReduceEntitlement(
                current, targetActive, webhook.OccurredUtc);
            decisionCode = entitlementTransition.DecisionCode;
        }

        var subscriptionSha = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(webhook.SubscriptionId)));
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO webhook_events(
                event_id, transmission_id, event_type, subscription_sha256,
                payload_sha256, occurred_utc, decision_code)
            VALUES($event, $transmission, $type, $subscription_sha,
                $payload_sha, $occurred, $decision);
            """;
        command.Parameters.AddWithValue("$event", webhook.EventId);
        command.Parameters.AddWithValue("$transmission", webhook.TransmissionId);
        command.Parameters.AddWithValue("$type", webhook.EventType);
        command.Parameters.AddWithValue("$subscription_sha", subscriptionSha);
        command.Parameters.AddWithValue("$payload_sha", payloadSha);
        command.Parameters.AddWithValue("$occurred", webhook.OccurredUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$decision", decisionCode);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (entitlementTransition is { Applies: true } transition)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO entitlements(
                    subscription_id, active, updated_utc, source_event_id, source_event_type)
                VALUES($subscription, $active, $updated, $source_event, $source_type)
                ON CONFLICT(subscription_id) DO UPDATE SET
                    active = excluded.active,
                    updated_utc = excluded.updated_utc,
                    source_event_id = excluded.source_event_id,
                    source_event_type = excluded.source_event_type;
                """;
            command.Parameters.AddWithValue("$subscription", webhook.SubscriptionId);
            command.Parameters.AddWithValue("$active", transition.Active ? 1 : 0);
            command.Parameters.AddWithValue("$updated", webhook.OccurredUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$source_event", webhook.EventId);
            command.Parameters.AddWithValue("$source_type", webhook.EventType);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplySubscriptionFinancialInvariantAsync(
                connection, transaction, webhook, cancellationToken)
            .ConfigureAwait(false);
        var journalEntry = CreateWebhookEntry(webhook);
        if (journalEntry is not null)
        {
            await PostInternalAsync(connection, transaction, journalEntry, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsQuarantinableWebhookDependency(string code) =>
        code is "paypal_sandbox_subscription_mapping_missing" or
            "paypal_sandbox_sale_mapping_missing";

    private static async Task InsertWebhookQuarantineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayPalSandboxVerifiedWebhook verified,
        string expectedPlanId,
        string reasonCode,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken)
    {
        if (!IsQuarantinableWebhookDependency(reasonCode))
        {
            throw new InvalidOperationException(
                "Only a missing verified-event dependency may enter quarantine.");
        }

        var received = receivedUtc.ToUniversalTime();
        var verifiedSha = VerifiedWebhookSha(verified, expectedPlanId);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO webhook_event_quarantine(
                event_id, transmission_id, event_type, provider_object_id,
                subscription_id, plan_id, related_sale_id, amount_cents, currency,
                occurred_utc, payload_sha256, expected_plan_id, verified_event_sha256,
                reason_code, state, attempt_count, first_received_utc, next_attempt_utc)
            VALUES($event, $transmission, $type, $object,
                $subscription, $plan, $related_sale, $amount, $currency,
                $occurred, $payload, $expected_plan, $verified_sha,
                $reason, 'pending', 0, $received, $received);
            """;
        command.Parameters.AddWithValue("$event", verified.EventId);
        command.Parameters.AddWithValue("$transmission", verified.TransmissionId);
        command.Parameters.AddWithValue("$type", verified.EventType);
        command.Parameters.AddWithValue("$object", verified.ProviderObjectId);
        command.Parameters.AddWithValue("$subscription", (object?)verified.SubscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$plan", (object?)verified.PlanId ?? DBNull.Value);
        command.Parameters.AddWithValue("$related_sale", (object?)verified.RelatedSaleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", verified.AmountCents);
        command.Parameters.AddWithValue("$currency", verified.Currency);
        command.Parameters.AddWithValue("$occurred", verified.OccurredUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", verified.PayloadSha256);
        command.Parameters.AddWithValue("$expected_plan", expectedPlanId);
        command.Parameters.AddWithValue("$verified_sha", verifiedSha);
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$received", received.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WebhookQuarantineIdentity?> ReadWebhookQuarantineIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string transmissionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, transmission_id, payload_sha256
            FROM webhook_event_quarantine
            WHERE event_id = $event OR transmission_id = $transmission;
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$transmission", transmissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WebhookQuarantineIdentity(
                reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task<DurableWebhookQuarantineRecord?> ReadWebhookQuarantineAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT transmission_id, event_type, provider_object_id,
                   subscription_id, plan_id, related_sale_id,
                   amount_cents, currency, occurred_utc, payload_sha256,
                   expected_plan_id, verified_event_sha256, reason_code, state,
                   attempt_count, first_received_utc, last_attempt_utc,
                   next_attempt_utc, completed_utc
            FROM webhook_event_quarantine WHERE event_id = $event;
            """;
        command.Parameters.AddWithValue("$event", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new DurableWebhookQuarantineRecord(
            eventId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt32(14),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : DateTimeOffset.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : DateTimeOffset.Parse(reader.GetString(18)));
    }

    private static async Task MarkWebhookQuarantineCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableWebhookQuarantineRecord record,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE webhook_event_quarantine
            SET state = 'completed', reason_code = 'dependency_resolved',
                attempt_count = attempt_count + 1,
                last_attempt_utc = $completed, next_attempt_utc = NULL,
                completed_utc = $completed
            WHERE event_id = $event AND state = 'pending'
              AND verified_event_sha256 = $verified_sha;
            """;
        command.Parameters.AddWithValue("$completed", completedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$event", record.EventId);
        command.Parameters.AddWithValue("$verified_sha", record.VerifiedEventSha256);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The quarantined webhook completion conflicted with durable state.");
        }
    }

    private static async Task MarkWebhookQuarantineFailureAsync(
        SqliteConnection connection,
        DurableWebhookQuarantineRecord record,
        string reasonCode,
        bool keepPending,
        DateTimeOffset attemptedUtc,
        CancellationToken cancellationToken)
    {
        var attemptCount = checked(record.AttemptCount + 1);
        var nextAttemptUtc = keepPending
            ? attemptedUtc.AddSeconds(Math.Min(3600, 1 << Math.Min(attemptCount, 10)))
            : (DateTimeOffset?)null;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE webhook_event_quarantine
            SET state = $state, reason_code = $reason,
                attempt_count = $attempts, last_attempt_utc = $attempted,
                next_attempt_utc = $next
            WHERE event_id = $event AND state = 'pending'
              AND verified_event_sha256 = $verified_sha
              AND attempt_count = $prior_attempts;
            """;
        command.Parameters.AddWithValue("$state", keepPending ? "pending" : "blocked");
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$attempts", attemptCount);
        command.Parameters.AddWithValue("$attempted", attemptedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$next", (object?)nextAttemptUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", record.EventId);
        command.Parameters.AddWithValue("$verified_sha", record.VerifiedEventSha256);
        command.Parameters.AddWithValue("$prior_attempts", record.AttemptCount);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                "The quarantined webhook retry state conflicted with durable state.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PayPalSandboxVerifiedWebhook ToVerifiedWebhook(
        DurableWebhookQuarantineRecord record) => new(
        record.EventId,
        record.TransmissionId,
        record.EventType,
        record.ProviderObjectId,
        record.SubscriptionId,
        record.PlanId,
        record.RelatedSaleId,
        record.AmountCents,
        record.Currency,
        record.OccurredUtc,
        record.PayloadSha256);

    private static string VerifiedWebhookSha(
        PayPalSandboxVerifiedWebhook verified,
        string expectedPlanId) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            verified.EventId,
            verified.TransmissionId,
            verified.EventType,
            verified.ProviderObjectId,
            verified.SubscriptionId,
            verified.PlanId,
            verified.RelatedSaleId,
            verified.AmountCents,
            verified.Currency,
            OccurredUtc = verified.OccurredUtc.ToUniversalTime(),
            verified.PayloadSha256,
            ExpectedPlanId = expectedPlanId,
        })));

    private static void ValidateWebhookQuarantineRecord(
        DurableWebhookQuarantineRecord record)
    {
        RequireWebhookQuarantineIdentifier(record.EventId, 128, "event");
        RequireWebhookQuarantineIdentifier(record.TransmissionId, 50, "transmission");
        RequireWebhookQuarantineIdentifier(record.ProviderObjectId, 128, "provider object");
        if (record.SubscriptionId is not null)
            RequireWebhookQuarantineIdentifier(record.SubscriptionId, 50, "subscription");
        if (record.PlanId is not null)
            PayPalSandboxValidation.RequireSubscriptionPlanId(record.PlanId);
        if (record.RelatedSaleId is not null)
            RequireWebhookQuarantineIdentifier(record.RelatedSaleId, 128, "related sale");
        PayPalSandboxValidation.RequireSubscriptionPlanId(record.ExpectedPlanId);
        if (record.EventType is not ("PAYMENT.SALE.COMPLETED" or
                "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED") ||
            record.AmountCents is < 1 or > 300 || record.Currency != "USD" ||
            record.OccurredUtc.Offset != TimeSpan.Zero ||
            record.FirstReceivedUtc.Offset != TimeSpan.Zero ||
            record.LastAttemptUtc is { } lastAttemptUtc && lastAttemptUtc.Offset != TimeSpan.Zero ||
            record.NextAttemptUtc is { } nextAttemptUtc && nextAttemptUtc.Offset != TimeSpan.Zero ||
            record.CompletedUtc is { } completedUtc && completedUtc.Offset != TimeSpan.Zero ||
            !PayoutContractValidation.IsLowerSha256(record.PayloadSha256) ||
            !PayoutContractValidation.IsLowerSha256(record.VerifiedEventSha256) ||
            record.AttemptCount < 0 || record.AttemptCount > 1_000_000 ||
            record.State is not ("pending" or "completed" or "blocked") ||
            record.State == "pending" && (record.NextAttemptUtc is null || record.CompletedUtc is not null) ||
            record.State == "completed" && (record.NextAttemptUtc is not null || record.CompletedUtc is null) ||
            record.State == "blocked" && (record.NextAttemptUtc is not null || record.CompletedUtc is not null) ||
            record.State == "pending" && !IsQuarantinableWebhookDependency(record.ReasonCode) ||
            record.State == "completed" && record.ReasonCode != "dependency_resolved" ||
            record.State == "blocked" && record.ReasonCode is not (
                "paypal_sandbox_quarantine_identity_conflict" or
                "paypal_sandbox_quarantine_reprocess_blocked") ||
            record.LastAttemptUtc is { } last && last < record.FirstReceivedUtc ||
            record.CompletedUtc is { } completed && completed < record.FirstReceivedUtc)
        {
            throw new InvalidOperationException(
                "The quarantined verified webhook record is invalid.");
        }

        var expectedSha = VerifiedWebhookSha(ToVerifiedWebhook(record), record.ExpectedPlanId);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedSha),
                Convert.FromHexString(record.VerifiedEventSha256)))
        {
            throw new InvalidOperationException(
                "The quarantined verified webhook failed its stored digest.");
        }
    }

    private static void RequireWebhookQuarantineIdentifier(
        string value,
        int maximumLength,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not (':' or '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                $"The quarantined webhook {label} identifier is invalid.");
        }
    }

    private sealed record WebhookQuarantineIdentity(
        string EventId,
        string TransmissionId,
        string PayloadSha256);
}
