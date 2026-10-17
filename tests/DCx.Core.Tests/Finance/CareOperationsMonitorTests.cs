using DCx.Core.Finance;
using DCx.GlobalService;

namespace DCx.Core.Tests.Finance;

public sealed class CareOperationsMonitorTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-18T05:00:00Z");

    [Fact]
    public async Task PollOnce_EmitsOnlyChangedAttentionAndRecoveryStates()
    {
        var source = new SequenceSource(
            Health("clear", completed: 1),
            Health("clear", completed: 2),
            Health("retry_scheduled", pending: 1, next: Now.AddMinutes(1)),
            Health("retry_scheduled", pending: 1, next: Now.AddMinutes(1)),
            Health("retry_due", pending: 1, due: 1, canProcess: true),
            Health("operator_action_required", pending: 1, blocked: 1),
            Health("clear", completed: 3));
        var sink = new CapturingSink();
        var monitor = new CareOperationsMonitor(
            source, sink, new FixedTimeProvider(Now));

        for (var index = 0; index < 7; index++)
        {
            await monitor.PollOnceAsync();
        }

        Assert.Equal(7, source.RefreshCalls);
        Assert.Equal(0, source.ProcessCalls);
        Assert.Equal(
            [
                CareOperationsMonitor.RetryScheduledCode,
                CareOperationsMonitor.RetryDueCode,
                CareOperationsMonitor.OperatorActionRequiredCode,
                CareOperationsMonitor.RecoveredCode,
            ],
            sink.Alerts.Select(alert => alert.Code).ToArray());
        Assert.Equal(
            ["information", "warning", "critical", "information"],
            sink.Alerts.Select(alert => alert.Severity).ToArray());
        Assert.All(sink.Alerts, alert => Assert.Equal(Now, alert.ObservedUtc));
        Assert.Equal(0, sink.Alerts[^1].Health.Pending);
        Assert.Equal(3, sink.Alerts[^1].Health.Completed);
    }

    [Fact]
    public async Task ConcurrentPollAndFailedDelivery_DoNotDuplicateOrLoseTransition()
    {
        var due = Health("retry_due", pending: 1, due: 1, canProcess: true);
        var source = new SequenceSource(due, due, due);
        var sink = new CapturingSink(failFirst: true);
        var monitor = new CareOperationsMonitor(source, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.PollOnceAsync());
        var attempts = await Task.WhenAll(
            monitor.PollOnceAsync(),
            monitor.PollOnceAsync());

        Assert.Equal(3, source.RefreshCalls);
        Assert.Equal(0, source.ProcessCalls);
        Assert.Equal(2, sink.PublishCalls);
        Assert.Single(sink.Alerts);
        Assert.Single(attempts, alert => alert is not null);
        Assert.Single(attempts, alert => alert is null);
        Assert.Equal(CareOperationsMonitor.RetryDueCode, sink.Alerts[0].Code);
    }

    [Fact]
    public void AggregateAlertShape_HasNoIdentifierOrFreeFormMessageField()
    {
        Assert.Equal(
            ["Code", "Severity", "Health", "ObservedUtc"],
            typeof(CareOperationsAggregateAlert)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
        Assert.DoesNotContain(
            typeof(CareOperationsAggregateAlert).GetProperties(),
            property => property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase));
    }

    private static CareOperationsHealth Health(
        string status,
        int pending = 0,
        int due = 0,
        int blocked = 0,
        int completed = 0,
        DateTimeOffset? next = null,
        bool canProcess = false) => new(
            status,
            pending,
            due,
            blocked,
            completed,
            MaximumAttemptCount: pending + blocked,
            OldestPendingUtc: pending > 0 ? Now.AddMinutes(-1) : null,
            NextAttemptUtc: next ?? (pending > 0 ? Now : null),
            CanProcessDue: canProcess);

    private sealed class SequenceSource(params CareOperationsHealth[] health)
        : ICareOperationsSource
    {
        private readonly Queue<CareOperationsHealth> states = new(health);
        private CareOperationsHealth? last;

        internal int RefreshCalls { get; private set; }
        internal int ProcessCalls { get; private set; }

        public Task<CareOperationsHealth> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (states.Count > 0) last = states.Dequeue();
            return Task.FromResult(last ?? throw new InvalidOperationException("No health fixture."));
        }

        public Task<CareOperationsProcessResult> ProcessDueAsync(
            CareOperationsHealth reviewedHealth,
            bool explicitlyApproved,
            CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            throw new InvalidOperationException("The monitor must never process CARE events.");
        }
    }

    private sealed class CapturingSink(bool failFirst = false) : ICareOperationsAlertSink
    {
        internal List<CareOperationsAggregateAlert> Alerts { get; } = [];
        internal int PublishCalls { get; private set; }

        public Task PublishAsync(
            CareOperationsAggregateAlert alert,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishCalls++;
            if (failFirst && PublishCalls == 1)
            {
                throw new InvalidOperationException("Synthetic sink failure.");
            }

            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
