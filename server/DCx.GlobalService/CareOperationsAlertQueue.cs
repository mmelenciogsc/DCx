using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

internal sealed record CareOperationsQueuedAlert(
    long Sequence,
    CareOperationsAggregateAlert Alert,
    string PreviousRecordSha256,
    string RecordSha256);

internal sealed record CareOperationsAlertDelivery(
    long Sequence,
    string RecordSha256,
    DateTimeOffset DeliveredUtc,
    string DeliverySha256);

internal interface ICareOperationsAlertQueue
{
    Task<CareOperationsQueuedAlert?> ObserveAsync(
        CareOperationsHealth health,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default);

    Task<bool> MarkDeliveredAsync(
        long sequence,
        string recordSha256,
        DateTimeOffset deliveredUtc,
        CancellationToken cancellationToken = default);
}

internal static class CareOperationsAlertIntegrity
{
    internal static readonly string GenesisSha256 = new('0', 64);

    internal static string FingerprintSha256(CareOperationsHealth health)
    {
        CareOperationsPolicy.RequireHealth(health);
        return Digest(
            "care-operations-alert-fingerprint-v1",
            health.StatusCode,
            health.Pending.ToString(CultureInfo.InvariantCulture),
            health.Due.ToString(CultureInfo.InvariantCulture),
            health.Blocked.ToString(CultureInfo.InvariantCulture),
            health.MaximumAttemptCount.ToString(CultureInfo.InvariantCulture),
            FormatUtc(health.OldestPendingUtc),
            FormatUtc(health.NextAttemptUtc),
            health.CanProcessDue ? "1" : "0");
    }

    internal static string StateSha256(
        string statusCode,
        string fingerprintSha256,
        DateTimeOffset observedUtc) =>
        Digest(
            "care-operations-monitor-state-v1",
            statusCode,
            fingerprintSha256,
            FormatUtc(observedUtc));

    internal static CareOperationsQueuedAlert CreateQueuedAlert(
        long sequence,
        string previousRecordSha256,
        string previousStatusCode,
        CareOperationsHealth health,
        DateTimeOffset observedUtc)
    {
        if (sequence < 1 || !IsLowerSha256(previousRecordSha256))
        {
            throw new InvalidOperationException("The CARE alert sequence is invalid.");
        }

        CareOperationsPolicy.RequireHealth(health);
        if (health.StatusCode == "clear" && previousStatusCode == "clear")
        {
            throw new InvalidOperationException("An unchanged clear CARE state is not alertable.");
        }

        var alert = new CareOperationsAggregateAlert(
            health.StatusCode switch
            {
                "retry_scheduled" => CareOperationsMonitor.RetryScheduledCode,
                "retry_due" => CareOperationsMonitor.RetryDueCode,
                "operator_action_required" => CareOperationsMonitor.OperatorActionRequiredCode,
                "clear" => CareOperationsMonitor.RecoveredCode,
                _ => throw new InvalidOperationException(
                    "The CARE aggregate monitor status is not alertable."),
            },
            health.StatusCode switch
            {
                "operator_action_required" => "critical",
                "retry_due" => "warning",
                _ => "information",
            },
            health,
            observedUtc.ToUniversalTime());
        var queued = new CareOperationsQueuedAlert(
            sequence, alert, previousRecordSha256, string.Empty);
        queued = queued with { RecordSha256 = RecordSha256(queued) };
        RequireQueuedAlert(queued);
        return queued;
    }

    internal static void RequireQueuedAlert(CareOperationsQueuedAlert queued)
    {
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(queued.Alert);
        CareOperationsPolicy.RequireHealth(queued.Alert.Health);
        if (queued.Sequence < 1 || queued.Alert.ObservedUtc.Offset != TimeSpan.Zero ||
            !IsCodeAndSeverity(queued.Alert.Code, queued.Alert.Severity) ||
            !IsLowerSha256(queued.PreviousRecordSha256) ||
            !IsLowerSha256(queued.RecordSha256) ||
            queued.Alert.Code != CodeFor(queued.Alert.Health.StatusCode))
        {
            throw new InvalidOperationException("The CARE queued alert is invalid.");
        }

        var expected = RecordSha256(queued with { RecordSha256 = string.Empty });
        if (!FixedTimeShaEquals(expected, queued.RecordSha256))
        {
            throw new InvalidOperationException("The CARE queued alert failed its digest.");
        }
    }

    internal static CareOperationsAlertDelivery CreateDelivery(
        CareOperationsQueuedAlert queued,
        DateTimeOffset deliveredUtc)
    {
        RequireQueuedAlert(queued);
        if (deliveredUtc.ToUniversalTime() < queued.Alert.ObservedUtc)
        {
            throw new InvalidOperationException(
                "The CARE alert delivery predates observation.");
        }

        var delivery = new CareOperationsAlertDelivery(
            queued.Sequence,
            queued.RecordSha256,
            deliveredUtc.ToUniversalTime(),
            string.Empty);
        delivery = delivery with { DeliverySha256 = DeliverySha256(delivery) };
        RequireDelivery(delivery);
        return delivery;
    }

    internal static void RequireDelivery(CareOperationsAlertDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Sequence < 1 || delivery.DeliveredUtc.Offset != TimeSpan.Zero ||
            !IsLowerSha256(delivery.RecordSha256) ||
            !IsLowerSha256(delivery.DeliverySha256) ||
            !FixedTimeShaEquals(
                DeliverySha256(delivery with { DeliverySha256 = string.Empty }),
                delivery.DeliverySha256))
        {
            throw new InvalidOperationException("The CARE alert delivery receipt is invalid.");
        }
    }

    internal static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CodeFor(string statusCode) => statusCode switch
    {
        "retry_scheduled" => CareOperationsMonitor.RetryScheduledCode,
        "retry_due" => CareOperationsMonitor.RetryDueCode,
        "operator_action_required" => CareOperationsMonitor.OperatorActionRequiredCode,
        "clear" => CareOperationsMonitor.RecoveredCode,
        _ => string.Empty,
    };

    private static bool IsCodeAndSeverity(string code, string severity) =>
        (code, severity) is
            (CareOperationsMonitor.RetryScheduledCode, "information") or
            (CareOperationsMonitor.RetryDueCode, "warning") or
            (CareOperationsMonitor.OperatorActionRequiredCode, "critical") or
            (CareOperationsMonitor.RecoveredCode, "information");

    private static string RecordSha256(CareOperationsQueuedAlert queued) =>
        Digest(
            "care-operations-alert-record-v1",
            queued.Sequence.ToString(CultureInfo.InvariantCulture),
            queued.Alert.Code,
            queued.Alert.Severity,
            queued.Alert.Health.StatusCode,
            queued.Alert.Health.Pending.ToString(CultureInfo.InvariantCulture),
            queued.Alert.Health.Due.ToString(CultureInfo.InvariantCulture),
            queued.Alert.Health.Blocked.ToString(CultureInfo.InvariantCulture),
            queued.Alert.Health.Completed.ToString(CultureInfo.InvariantCulture),
            queued.Alert.Health.MaximumAttemptCount.ToString(CultureInfo.InvariantCulture),
            FormatUtc(queued.Alert.Health.OldestPendingUtc),
            FormatUtc(queued.Alert.Health.NextAttemptUtc),
            queued.Alert.Health.CanProcessDue ? "1" : "0",
            FormatUtc(queued.Alert.ObservedUtc),
            queued.PreviousRecordSha256);

    private static string DeliverySha256(CareOperationsAlertDelivery delivery) =>
        Digest(
            "care-operations-alert-delivery-v1",
            delivery.Sequence.ToString(CultureInfo.InvariantCulture),
            delivery.RecordSha256,
            FormatUtc(delivery.DeliveredUtc));

    private static bool FixedTimeShaEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static string? FormatUtc(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Digest(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            if (value is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBytes, -1);
                hash.AppendData(lengthBytes);
                continue;
            }

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
}

internal sealed class InMemoryCareOperationsAlertQueue : ICareOperationsAlertQueue
{
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private string? statusCode;
    private string? fingerprintSha256;
    private CareOperationsQueuedAlert? pending;
    private long sequence;
    private string previousRecordSha256 = CareOperationsAlertIntegrity.GenesisSha256;

    public async Task<CareOperationsQueuedAlert?> ObserveAsync(
        CareOperationsHealth health,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default)
    {
        CareOperationsPolicy.RequireHealth(health);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending is not null) return pending;
            var currentFingerprint = CareOperationsAlertIntegrity.FingerprintSha256(health);
            if (currentFingerprint == fingerprintSha256) return null;
            var previousStatus = statusCode;
            statusCode = health.StatusCode;
            fingerprintSha256 = currentFingerprint;
            if (health.StatusCode == "clear" &&
                (previousStatus is null or "clear"))
            {
                return null;
            }

            sequence = checked(sequence + 1);
            pending = CareOperationsAlertIntegrity.CreateQueuedAlert(
                sequence,
                previousRecordSha256,
                previousStatus ?? "clear",
                health,
                observedUtc);
            previousRecordSha256 = pending.RecordSha256;
            return pending;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<bool> MarkDeliveredAsync(
        long sequenceValue,
        string recordSha256,
        DateTimeOffset deliveredUtc,
        CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending is null) return false;
            if (pending.Sequence != sequenceValue || pending.RecordSha256 != recordSha256)
            {
                throw new InvalidOperationException("The CARE alert delivery binding changed.");
            }

            CareOperationsAlertIntegrity.CreateDelivery(pending, deliveredUtc);
            pending = null;
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }
}

public sealed partial class DurableCareRepository : ICareOperationsAlertQueue
{
    async Task<CareOperationsQueuedAlert?> ICareOperationsAlertQueue.ObserveAsync(
        CareOperationsHealth health,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken) =>
        await ObserveCareOperationsAlertAsync(
                health, observedUtc, cancellationToken)
            .ConfigureAwait(false);

    async Task<bool> ICareOperationsAlertQueue.MarkDeliveredAsync(
        long sequence,
        string recordSha256,
        DateTimeOffset deliveredUtc,
        CancellationToken cancellationToken) =>
        await MarkCareOperationsAlertDeliveredAsync(
                sequence, recordSha256, deliveredUtc, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<CareOperationsQueuedAlert?> ObserveCareOperationsAlertAsync(
        CareOperationsHealth health,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken = default)
    {
        CareOperationsPolicy.RequireHealth(health);
        var observed = observedUtc.ToUniversalTime();
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var history = await ReadCareOperationsAlertHistoryAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var delivered = history.Deliveries.Select(item => item.Sequence).ToHashSet();
            var pending = history.Alerts.FirstOrDefault(item => !delivered.Contains(item.Sequence));
            if (pending is not null) return pending;

            var state = await ReadCareOperationsMonitorStateAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = CareOperationsAlertIntegrity.FingerprintSha256(health);
            if (state is not null && state.Value.FingerprintSha256 == fingerprint)
            {
                return null;
            }

            var previousStatus = state?.StatusCode;
            await UpsertCareOperationsMonitorStateAsync(
                    connection, transaction, health.StatusCode, fingerprint, observed,
                    cancellationToken)
                .ConfigureAwait(false);
            CareOperationsQueuedAlert? queued = null;
            if (health.StatusCode != "clear" ||
                previousStatus is not null and not "clear")
            {
                var sequence = history.Alerts.Count == 0
                    ? 1
                    : checked(history.Alerts[^1].Sequence + 1);
                var previousSha256 = history.Alerts.Count == 0
                    ? CareOperationsAlertIntegrity.GenesisSha256
                    : history.Alerts[^1].RecordSha256;
                queued = CareOperationsAlertIntegrity.CreateQueuedAlert(
                    sequence,
                    previousSha256,
                    previousStatus ?? "clear",
                    health,
                    observed);
                await InsertCareOperationsAlertAsync(
                        connection, transaction, queued, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return queued;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    internal async Task<bool> MarkCareOperationsAlertDeliveredAsync(
        long sequence,
        string recordSha256,
        DateTimeOffset deliveredUtc,
        CancellationToken cancellationToken = default)
    {
        if (sequence < 1 || !CareOperationsAlertIntegrity.IsLowerSha256(recordSha256))
        {
            throw new ArgumentException("The CARE alert delivery binding is invalid.");
        }

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var history = await ReadCareOperationsAlertHistoryAsync(
                    connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var queued = history.Alerts.SingleOrDefault(item => item.Sequence == sequence)
                ?? throw new InvalidOperationException("The CARE queued alert does not exist.");
            if (queued.RecordSha256 != recordSha256)
            {
                throw new InvalidOperationException("The CARE alert delivery binding changed.");
            }

            var existing = history.Deliveries.SingleOrDefault(item => item.Sequence == sequence);
            if (existing is not null)
            {
                return false;
            }

            var deliveredSequences = history.Deliveries.Select(item => item.Sequence).ToHashSet();
            var oldestPending = history.Alerts.First(item => !deliveredSequences.Contains(item.Sequence));
            if (oldestPending.Sequence != sequence)
            {
                throw new InvalidOperationException("CARE alerts must be delivered in sequence.");
            }

            var delivery = CareOperationsAlertIntegrity.CreateDelivery(queued, deliveredUtc);
            await InsertCareOperationsAlertDeliveryAsync(
                    connection, transaction, delivery, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    internal async Task<(
        IReadOnlyList<CareOperationsQueuedAlert> Alerts,
        IReadOnlyList<CareOperationsAlertDelivery> Deliveries)>
        GetCareOperationsAlertHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCareOperationsAlertHistoryAsync(
                connection, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(
        IReadOnlyList<CareOperationsQueuedAlert> Alerts,
        IReadOnlyList<CareOperationsAlertDelivery> Deliveries)>
        ReadCareOperationsAlertHistoryAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var alerts = new List<CareOperationsQueuedAlert>();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, code, severity, status_code, pending, due, blocked,
                completed, maximum_attempt_count, oldest_pending_utc,
                next_attempt_utc, can_process_due, observed_utc,
                previous_record_sha256, record_sha256
            FROM care_operations_alert_outbox
            ORDER BY sequence;
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var health = new CareOperationsHealth(
                    reader.GetString(3),
                    checked((int)reader.GetInt64(4)),
                    checked((int)reader.GetInt64(5)),
                    checked((int)reader.GetInt64(6)),
                    checked((int)reader.GetInt64(7)),
                    checked((int)reader.GetInt64(8)),
                    reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : ParseUtc(reader.GetString(10)),
                    reader.GetInt64(11) == 1);
                var queued = new CareOperationsQueuedAlert(
                    reader.GetInt64(0),
                    new CareOperationsAggregateAlert(
                        reader.GetString(1),
                        reader.GetString(2),
                        health,
                        ParseUtc(reader.GetString(12))),
                    reader.GetString(13),
                    reader.GetString(14));
                CareOperationsAlertIntegrity.RequireQueuedAlert(queued);
                var expectedSequence = alerts.Count + 1L;
                var expectedPrevious = alerts.Count == 0
                    ? CareOperationsAlertIntegrity.GenesisSha256
                    : alerts[^1].RecordSha256;
                if (queued.Sequence != expectedSequence ||
                    queued.PreviousRecordSha256 != expectedPrevious)
                {
                    throw new InvalidOperationException("The CARE alert outbox chain is invalid.");
                }

                alerts.Add(queued);
            }
        }

        var deliveries = new List<CareOperationsAlertDelivery>();
        command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, record_sha256, delivered_utc, delivery_sha256
            FROM care_operations_alert_deliveries
            ORDER BY sequence;
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var delivery = new CareOperationsAlertDelivery(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    ParseUtc(reader.GetString(2)),
                    reader.GetString(3));
                CareOperationsAlertIntegrity.RequireDelivery(delivery);
                var queued = alerts.SingleOrDefault(item => item.Sequence == delivery.Sequence);
                if (queued is null || queued.RecordSha256 != delivery.RecordSha256 ||
                    delivery.DeliveredUtc < queued.Alert.ObservedUtc)
                {
                    throw new InvalidOperationException(
                        "The CARE alert delivery receipt is not bound to its outbox record.");
                }

                deliveries.Add(delivery);
            }
        }

        for (var index = 0; index < deliveries.Count; index++)
        {
            if (deliveries[index].Sequence != index + 1L)
            {
                throw new InvalidOperationException(
                    "The CARE alert delivery sequence has a gap.");
            }
        }

        return (alerts, deliveries);
    }

    private static async Task<(
        string StatusCode,
        string FingerprintSha256,
        DateTimeOffset ObservedUtc)?> ReadCareOperationsMonitorStateAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status_code, fingerprint_sha256, observed_utc, state_sha256
            FROM care_operations_monitor_state
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var status = reader.GetString(0);
        var fingerprint = reader.GetString(1);
        var observed = ParseUtc(reader.GetString(2));
        var stateSha256 = reader.GetString(3);
        if (!CareOperationsAlertIntegrity.IsLowerSha256(fingerprint) ||
            !CareOperationsAlertIntegrity.IsLowerSha256(stateSha256) ||
            stateSha256 != CareOperationsAlertIntegrity.StateSha256(
                status, fingerprint, observed) ||
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The CARE monitor state is invalid.");
        }

        return (status, fingerprint, observed);
    }

    private static async Task UpsertCareOperationsMonitorStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string statusCode,
        string fingerprintSha256,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO care_operations_monitor_state(
                singleton_id, status_code, fingerprint_sha256, observed_utc, state_sha256)
            VALUES(1, $status, $fingerprint, $observed, $state)
            ON CONFLICT(singleton_id) DO UPDATE SET
                status_code = excluded.status_code,
                fingerprint_sha256 = excluded.fingerprint_sha256,
                observed_utc = excluded.observed_utc,
                state_sha256 = excluded.state_sha256;
            """;
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$fingerprint", fingerprintSha256);
        command.Parameters.AddWithValue("$observed", observedUtc.ToString("O"));
        command.Parameters.AddWithValue("$state",
            CareOperationsAlertIntegrity.StateSha256(
                statusCode, fingerprintSha256, observedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCareOperationsAlertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CareOperationsQueuedAlert queued,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO care_operations_alert_outbox(
                sequence, code, severity, status_code, pending, due, blocked,
                completed, maximum_attempt_count, oldest_pending_utc,
                next_attempt_utc, can_process_due, observed_utc,
                previous_record_sha256, record_sha256)
            VALUES($sequence, $code, $severity, $status, $pending, $due, $blocked,
                $completed, $maximum, $oldest, $next, $canProcess, $observed,
                $previous, $record);
            """;
        command.Parameters.AddWithValue("$sequence", queued.Sequence);
        command.Parameters.AddWithValue("$code", queued.Alert.Code);
        command.Parameters.AddWithValue("$severity", queued.Alert.Severity);
        command.Parameters.AddWithValue("$status", queued.Alert.Health.StatusCode);
        command.Parameters.AddWithValue("$pending", queued.Alert.Health.Pending);
        command.Parameters.AddWithValue("$due", queued.Alert.Health.Due);
        command.Parameters.AddWithValue("$blocked", queued.Alert.Health.Blocked);
        command.Parameters.AddWithValue("$completed", queued.Alert.Health.Completed);
        command.Parameters.AddWithValue("$maximum", queued.Alert.Health.MaximumAttemptCount);
        command.Parameters.AddWithValue("$oldest",
            (object?)queued.Alert.Health.OldestPendingUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$next",
            (object?)queued.Alert.Health.NextAttemptUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$canProcess", queued.Alert.Health.CanProcessDue ? 1 : 0);
        command.Parameters.AddWithValue("$observed", queued.Alert.ObservedUtc.ToString("O"));
        command.Parameters.AddWithValue("$previous", queued.PreviousRecordSha256);
        command.Parameters.AddWithValue("$record", queued.RecordSha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCareOperationsAlertDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CareOperationsAlertDelivery delivery,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO care_operations_alert_deliveries(
                sequence, record_sha256, delivered_utc, delivery_sha256)
            VALUES($sequence, $record, $delivered, $delivery);
            """;
        command.Parameters.AddWithValue("$sequence", delivery.Sequence);
        command.Parameters.AddWithValue("$record", delivery.RecordSha256);
        command.Parameters.AddWithValue("$delivered", delivery.DeliveredUtc.ToString("O"));
        command.Parameters.AddWithValue("$delivery", delivery.DeliverySha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
}
