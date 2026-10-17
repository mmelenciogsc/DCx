using System.Text.Json;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Tests.Finance;

public sealed class CareOperatorActionAuditTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-care-operator-audit-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExpectedDecisions_PersistAsChainedMinimizedAppendOnlyRecords()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var now = DateTimeOffset.Parse("2026-10-18T04:00:00Z");
        var operatorPseudonymSha256 = new string('a', 64);
        var source = new DurableCareOperationsSource(
            repository,
            processingCapabilityEnabled: true,
            timeProvider: new FixedTimeProvider(now),
            operatorPseudonymSha256: operatorPseudonymSha256);
        var clear = await source.RefreshAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ProcessDueAsync(clear, explicitlyApproved: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ProcessDueAsync(clear, explicitlyApproved: true));
        var disabledSource = new DurableCareOperationsSource(
            repository,
            processingCapabilityEnabled: false,
            timeProvider: new FixedTimeProvider(now),
            operatorPseudonymSha256: operatorPseudonymSha256);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabledSource.ProcessDueAsync(clear, explicitlyApproved: true));

        var reopened = await DurableCareRepository.OpenAsync(root);
        var records = await reopened.GetCareOperatorActionAuditAsync();
        Assert.Equal(3, records.Count);
        Assert.Equal(
            [
                CareOperatorActionAuditIntegrity.ApprovalRequiredDecision,
                CareOperatorActionAuditIntegrity.NothingDueDecision,
                CareOperatorActionAuditIntegrity.ProcessingDisabledDecision,
            ],
            records.Select(record => record.DecisionCode).ToArray());
        Assert.Equal(CareOperatorActionAuditIntegrity.GenesisSha256,
            records[0].PreviousRecordSha256);
        Assert.Equal(records[0].RecordSha256, records[1].PreviousRecordSha256);
        Assert.Equal(records[1].RecordSha256, records[2].PreviousRecordSha256);
        Assert.All(records, record =>
        {
            Assert.Equal(operatorPseudonymSha256, record.OperatorPseudonymSha256);
            Assert.Equal(now, record.OccurredUtc);
            Assert.Null(record.ResultSha256);
        });
        var serialized = JsonSerializer.Serialize(records);
        Assert.DoesNotContain("subscription", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event", serialized, StringComparison.OrdinalIgnoreCase);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE care_operator_action_audit
                SET decision_code = 'processing_disabled'
                WHERE sequence = 1;
                """;
            var updateError = await Assert.ThrowsAsync<SqliteException>(() =>
                update.ExecuteNonQueryAsync());
            Assert.Contains("append-only", updateError.Message, StringComparison.Ordinal);

            var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM care_operator_action_audit WHERE sequence = 1;";
            var deleteError = await Assert.ThrowsAsync<SqliteException>(() =>
                delete.ExecuteNonQueryAsync());
            Assert.Contains("append-only", deleteError.Message, StringComparison.Ordinal);

            var privilegedTamper = connection.CreateCommand();
            privilegedTamper.CommandText = """
                DROP TRIGGER care_operator_action_audit_no_update;
                UPDATE care_operator_action_audit
                SET decision_code = 'processing_disabled'
                WHERE sequence = 1;
                """;
            Assert.Equal(1, await privilegedTamper.ExecuteNonQueryAsync());
        }

        var integrityError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.GetCareOperatorActionAuditAsync());
        Assert.Contains("failed its digest", integrityError.Message, StringComparison.Ordinal);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
