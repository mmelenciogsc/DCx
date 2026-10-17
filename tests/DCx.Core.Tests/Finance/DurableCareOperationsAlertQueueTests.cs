using System.Text.Json;
using DCx.Core.Finance;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Tests.Finance;

public sealed class DurableCareOperationsAlertQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-18T06:00:00Z");
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-care-alert-queue-{Guid.NewGuid():N}");

    [Fact]
    public async Task AlertOutbox_SurvivesRestartOrdersRecoveryAndBindsDelivery()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        Assert.Null(await repository.ObserveCareOperationsAlertAsync(Clear(), Now));
        var due = Due();
        var first = (await repository.ObserveCareOperationsAlertAsync(due, Now.AddSeconds(1)))!;
        Assert.Equal(1, first.Sequence);
        Assert.Equal(CareOperationsMonitor.RetryDueCode, first.Alert.Code);

        var reopened = await DurableCareRepository.OpenAsync(root);
        var retry = (await reopened.ObserveCareOperationsAlertAsync(
            due, Now.AddSeconds(2)))!;
        Assert.Equal(first, retry);
        Assert.True(await reopened.MarkCareOperationsAlertDeliveredAsync(
            first.Sequence, first.RecordSha256, Now.AddSeconds(3)));
        Assert.False(await reopened.MarkCareOperationsAlertDeliveredAsync(
            first.Sequence, first.RecordSha256, Now.AddSeconds(4)));
        Assert.Null(await reopened.ObserveCareOperationsAlertAsync(
            due, Now.AddSeconds(5)));

        var blocked = Blocked();
        var second = (await reopened.ObserveCareOperationsAlertAsync(
            blocked, Now.AddSeconds(6)))!;
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.RecordSha256, second.PreviousRecordSha256);
        Assert.Equal(CareOperationsMonitor.OperatorActionRequiredCode, second.Alert.Code);
        Assert.True(await reopened.MarkCareOperationsAlertDeliveredAsync(
            second.Sequence, second.RecordSha256, Now.AddSeconds(7)));

        var recovered = (await reopened.ObserveCareOperationsAlertAsync(
            Clear(completed: 1), Now.AddSeconds(8)))!;
        Assert.Equal(3, recovered.Sequence);
        Assert.Equal(second.RecordSha256, recovered.PreviousRecordSha256);
        Assert.Equal(CareOperationsMonitor.RecoveredCode, recovered.Alert.Code);
        Assert.True(await reopened.MarkCareOperationsAlertDeliveredAsync(
            recovered.Sequence, recovered.RecordSha256, Now.AddSeconds(9)));

        var history = await (await DurableCareRepository.OpenAsync(root))
            .GetCareOperationsAlertHistoryAsync();
        Assert.Equal([1L, 2L, 3L],
            history.Alerts.Select(item => item.Sequence).ToArray());
        Assert.Equal([1L, 2L, 3L],
            history.Deliveries.Select(item => item.Sequence).ToArray());
        var serialized = JsonSerializer.Serialize(history);
        Assert.DoesNotContain("event", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subscription", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contributor", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlertOutbox_SerializesRepositoriesAndDetectsOrdinaryTamper()
    {
        var firstRepository = await DurableCareRepository.OpenAsync(root);
        var secondRepository = await DurableCareRepository.OpenAsync(root);
        var attempts = await Task.WhenAll(
            firstRepository.ObserveCareOperationsAlertAsync(Due(), Now),
            secondRepository.ObserveCareOperationsAlertAsync(Due(), Now));
        Assert.NotNull(attempts[0]);
        Assert.Equal(attempts[0], attempts[1]);
        var history = await firstRepository.GetCareOperationsAlertHistoryAsync();
        Assert.Single(history.Alerts);
        Assert.Empty(history.Deliveries);

        var acknowledgments = await Task.WhenAll(
            firstRepository.MarkCareOperationsAlertDeliveredAsync(
                attempts[0]!.Sequence, attempts[0]!.RecordSha256, Now.AddSeconds(1)),
            secondRepository.MarkCareOperationsAlertDeliveredAsync(
                attempts[0]!.Sequence, attempts[0]!.RecordSha256, Now.AddSeconds(1)));
        Assert.Single(acknowledgments, acknowledged => acknowledged);
        Assert.Single(acknowledgments, acknowledged => !acknowledged);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "care-ledger.sqlite3"),
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var updateOutbox = connection.CreateCommand();
        updateOutbox.CommandText = """
            UPDATE care_operations_alert_outbox
            SET severity = 'critical'
            WHERE sequence = 1;
            """;
        var outboxError = await Assert.ThrowsAsync<SqliteException>(() =>
            updateOutbox.ExecuteNonQueryAsync());
        Assert.Contains("append-only", outboxError.Message, StringComparison.Ordinal);

        var deleteDelivery = connection.CreateCommand();
        deleteDelivery.CommandText =
            "DELETE FROM care_operations_alert_deliveries WHERE sequence = 1;";
        var deliveryError = await Assert.ThrowsAsync<SqliteException>(() =>
            deleteDelivery.ExecuteNonQueryAsync());
        Assert.Contains("append-only", deliveryError.Message, StringComparison.Ordinal);

        var tamperState = connection.CreateCommand();
        tamperState.CommandText = """
            UPDATE care_operations_monitor_state
            SET status_code = 'clear'
            WHERE singleton_id = 1;
            """;
        Assert.Equal(1, await tamperState.ExecuteNonQueryAsync());
        var integrityError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstRepository.ObserveCareOperationsAlertAsync(Blocked(), Now.AddSeconds(2)));
        Assert.Contains("monitor state is invalid", integrityError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_RetriesPendingAlertAcrossRepositoryAndMonitorRestart()
    {
        var repository = await DurableCareRepository.OpenAsync(root);
        var firstSource = new StaticSource(Due());
        var failedSink = new CapturingSink(fail: true);
        var firstMonitor = new CareOperationsMonitor(
            firstSource,
            failedSink,
            repository,
            new FixedTimeProvider(Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstMonitor.PollOnceAsync());
        Assert.Equal(0, firstSource.ProcessCalls);

        var pendingHistory = await repository.GetCareOperationsAlertHistoryAsync();
        Assert.Single(pendingHistory.Alerts);
        Assert.Empty(pendingHistory.Deliveries);

        var reopened = await DurableCareRepository.OpenAsync(root);
        var secondSource = new StaticSource(Due());
        var deliveredSink = new CapturingSink();
        var secondMonitor = new CareOperationsMonitor(
            secondSource,
            deliveredSink,
            reopened,
            new FixedTimeProvider(Now.AddSeconds(1)));
        var delivered = await secondMonitor.PollOnceAsync();
        Assert.NotNull(delivered);
        Assert.Equal(CareOperationsMonitor.RetryDueCode, delivered.Code);
        Assert.Null(await secondMonitor.PollOnceAsync());
        Assert.Equal(0, secondSource.ProcessCalls);
        Assert.Single(deliveredSink.Alerts);
        var finalHistory = await reopened.GetCareOperationsAlertHistoryAsync();
        Assert.Single(finalHistory.Alerts);
        Assert.Single(finalHistory.Deliveries);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static CareOperationsHealth Clear(int completed = 0) => new(
        "clear", 0, 0, 0, completed, 0, null, null, false);

    private static CareOperationsHealth Due() => new(
        "retry_due", 1, 1, 0, 0, 1,
        Now.AddMinutes(-1), Now, true);

    private static CareOperationsHealth Blocked() => new(
        "operator_action_required", 1, 0, 1, 0, 2,
        Now.AddMinutes(-2), Now.AddMinutes(1), false);

    private sealed class StaticSource(CareOperationsHealth health) : ICareOperationsSource
    {
        internal int ProcessCalls { get; private set; }

        public Task<CareOperationsHealth> RefreshAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(health);

        public Task<CareOperationsProcessResult> ProcessDueAsync(
            CareOperationsHealth reviewedHealth,
            bool explicitlyApproved,
            CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            throw new InvalidOperationException("The monitor must not process CARE work.");
        }
    }

    private sealed class CapturingSink(bool fail = false) : ICareOperationsAlertSink
    {
        internal List<CareOperationsAggregateAlert> Alerts { get; } = [];

        public Task PublishAsync(
            CareOperationsAggregateAlert alert,
            CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException("Synthetic sink failure.");
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
