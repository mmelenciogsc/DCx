using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

internal sealed record DurableCareOperatorActionAuditRecord(
    long Sequence,
    DateTimeOffset OccurredUtc,
    string OperatorPseudonymSha256,
    string ActionCode,
    string DecisionCode,
    string ReviewedHealthSha256,
    string? ResultSha256,
    string PreviousRecordSha256,
    string RecordSha256);

internal static class CareOperatorActionAuditIntegrity
{
    internal const string ProcessDueAction = "process_due";
    internal const string ApprovalRequiredDecision = "approval_required";
    internal const string ProcessingDisabledDecision = "processing_disabled";
    internal const string ReviewStaleDecision = "review_stale";
    internal const string NothingDueDecision = "nothing_due";
    internal const string ProcessedDecision = "processed";
    internal static readonly string GenesisSha256 = new('0', 64);

    internal static string NormalizeOperatorPseudonymSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "The CARE operator pseudonym must be a SHA-256 digest.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    internal static string ReviewedHealthSha256(
        CareOperationsHealth health,
        bool explicitlyApproved)
    {
        CareOperationsPolicy.RequireHealth(health);
        return Digest(
            "care-operator-reviewed-health-v1",
            health.StatusCode,
            health.Pending.ToString(CultureInfo.InvariantCulture),
            health.Due.ToString(CultureInfo.InvariantCulture),
            health.Blocked.ToString(CultureInfo.InvariantCulture),
            health.Completed.ToString(CultureInfo.InvariantCulture),
            health.MaximumAttemptCount.ToString(CultureInfo.InvariantCulture),
            FormatUtc(health.OldestPendingUtc),
            FormatUtc(health.NextAttemptUtc),
            health.CanProcessDue ? "1" : "0",
            explicitlyApproved ? "1" : "0");
    }

    internal static string ResultSha256(CareOperationsProcessResult result)
    {
        CareOperationsPolicy.RequireProcessResult(result);
        return Digest(
            "care-operator-process-result-v1",
            result.Scanned.ToString(CultureInfo.InvariantCulture),
            result.Completed.ToString(CultureInfo.InvariantCulture),
            result.StillPending.ToString(CultureInfo.InvariantCulture),
            result.Blocked.ToString(CultureInfo.InvariantCulture),
            result.ProviderCalls.ToString(CultureInfo.InvariantCulture),
            ReviewedHealthSha256(result.Health, explicitlyApproved: false));
    }

    internal static void RequireRecord(DurableCareOperatorActionAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Sequence < 1 || record.OccurredUtc.Offset != TimeSpan.Zero ||
            NormalizeOperatorPseudonymSha256(record.OperatorPseudonymSha256) !=
            record.OperatorPseudonymSha256 ||
            record.ActionCode != ProcessDueAction ||
            !IsDecision(record.DecisionCode) ||
            !IsLowerSha256(record.ReviewedHealthSha256) ||
            (record.ResultSha256 is not null && !IsLowerSha256(record.ResultSha256)) ||
            !IsLowerSha256(record.PreviousRecordSha256) ||
            !IsLowerSha256(record.RecordSha256) ||
            (record.DecisionCode == ProcessedDecision) != (record.ResultSha256 is not null))
        {
            throw new InvalidOperationException("The CARE operator audit record is invalid.");
        }

        var expected = RecordSha256(record with { RecordSha256 = string.Empty });
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(record.RecordSha256)))
        {
            throw new InvalidOperationException("The CARE operator audit record failed its digest.");
        }
    }

    internal static string RecordSha256(DurableCareOperatorActionAuditRecord record) =>
        Digest(
            "care-operator-action-audit-v1",
            record.Sequence.ToString(CultureInfo.InvariantCulture),
            FormatUtc(record.OccurredUtc),
            record.OperatorPseudonymSha256,
            record.ActionCode,
            record.DecisionCode,
            record.ReviewedHealthSha256,
            record.ResultSha256,
            record.PreviousRecordSha256);

    private static bool IsDecision(string value) => value is
        ApprovalRequiredDecision or
        ProcessingDisabledDecision or
        ReviewStaleDecision or
        NothingDueDecision or
        ProcessedDecision;

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

public sealed partial class DurableCareRepository
{
    internal async Task<IReadOnlyList<DurableCareOperatorActionAuditRecord>>
        GetCareOperatorActionAuditAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAndVerifyCareOperatorActionAuditAsync(
                connection, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AppendCareOperatorActionAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operatorPseudonymSha256,
        string decisionCode,
        string reviewedHealthSha256,
        string? resultSha256,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAndVerifyCareOperatorActionAuditAsync(
                connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var sequence = existing.Count == 0 ? 1 : checked(existing[^1].Sequence + 1);
        var previousSha256 = existing.Count == 0
            ? CareOperatorActionAuditIntegrity.GenesisSha256
            : existing[^1].RecordSha256;
        var record = new DurableCareOperatorActionAuditRecord(
            sequence,
            occurredUtc.ToUniversalTime(),
            CareOperatorActionAuditIntegrity.NormalizeOperatorPseudonymSha256(
                operatorPseudonymSha256),
            CareOperatorActionAuditIntegrity.ProcessDueAction,
            decisionCode,
            reviewedHealthSha256,
            resultSha256,
            previousSha256,
            string.Empty);
        record = record with
        {
            RecordSha256 = CareOperatorActionAuditIntegrity.RecordSha256(record),
        };
        CareOperatorActionAuditIntegrity.RequireRecord(record);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO care_operator_action_audit(
                sequence, occurred_utc, operator_pseudonym_sha256, action_code,
                decision_code, reviewed_health_sha256, result_sha256,
                previous_record_sha256, record_sha256)
            VALUES($sequence, $occurred, $operator, $action, $decision, $review,
                $result, $previous, $record);
            """;
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$occurred", record.OccurredUtc.ToString("O"));
        command.Parameters.AddWithValue("$operator", record.OperatorPseudonymSha256);
        command.Parameters.AddWithValue("$action", record.ActionCode);
        command.Parameters.AddWithValue("$decision", record.DecisionCode);
        command.Parameters.AddWithValue("$review", record.ReviewedHealthSha256);
        command.Parameters.AddWithValue("$result", (object?)record.ResultSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", record.PreviousRecordSha256);
        command.Parameters.AddWithValue("$record", record.RecordSha256);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The CARE operator audit append was incomplete.");
        }
    }

    private static async Task<IReadOnlyList<DurableCareOperatorActionAuditRecord>>
        ReadAndVerifyCareOperatorActionAuditAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, occurred_utc, operator_pseudonym_sha256, action_code,
                decision_code, reviewed_health_sha256, result_sha256,
                previous_record_sha256, record_sha256
            FROM care_operator_action_audit
            ORDER BY sequence;
            """;
        var records = new List<DurableCareOperatorActionAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = new DurableCareOperatorActionAuditRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)
                    .ToUniversalTime(),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8));
            CareOperatorActionAuditIntegrity.RequireRecord(record);
            var expectedSequence = records.Count + 1L;
            var expectedPrevious = records.Count == 0
                ? CareOperatorActionAuditIntegrity.GenesisSha256
                : records[^1].RecordSha256;
            if (record.Sequence != expectedSequence ||
                !string.Equals(
                    record.PreviousRecordSha256, expectedPrevious, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The CARE operator audit chain is invalid.");
            }

            records.Add(record);
        }

        return records;
    }
}
