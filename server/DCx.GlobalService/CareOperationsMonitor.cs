using DCx.Core.Finance;

namespace DCx.GlobalService;

internal sealed record SimulationCareOperationsMonitorOptions(TimeSpan PollInterval)
{
    internal static SimulationCareOperationsMonitorOptions? Load(bool careOperationsApiEnabled)
    {
        var enabled = Environment.GetEnvironmentVariable("DCX_GLOBAL_CARE_MONITOR");
        var intervalText = Environment.GetEnvironmentVariable(
            "DCX_GLOBAL_CARE_MONITOR_INTERVAL_MS");
        if (enabled is null && intervalText is null) return null;
        if (enabled is not "1")
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_MONITOR must be 1 when CARE monitor settings are present.");
        }

        if (!careOperationsApiEnabled)
        {
            throw new InvalidOperationException(
                "The CARE monitor cannot run while the CARE operations API is disabled.");
        }

        var intervalMilliseconds = 30_000;
        if (intervalText is not null &&
            (!int.TryParse(intervalText, out intervalMilliseconds) ||
             intervalMilliseconds is < 100 or > 300_000))
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_MONITOR_INTERVAL_MS must be an integer from 100 through 300000.");
        }

        return new SimulationCareOperationsMonitorOptions(
            TimeSpan.FromMilliseconds(intervalMilliseconds));
    }
}

internal sealed record CareOperationsAggregateAlert(
    string Code,
    string Severity,
    CareOperationsHealth Health,
    DateTimeOffset ObservedUtc);

internal interface ICareOperationsAlertSink
{
    Task PublishAsync(
        CareOperationsAggregateAlert alert,
        CancellationToken cancellationToken = default);
}

internal sealed class LoggingCareOperationsAlertSink(
    ILogger<LoggingCareOperationsAlertSink> logger) : ICareOperationsAlertSink
{
    public Task PublishAsync(
        CareOperationsAggregateAlert alert,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        cancellationToken.ThrowIfCancellationRequested();
        if (alert.Code == CareOperationsMonitor.RecoveredCode)
        {
            logger.LogInformation(
                "CARE aggregate monitor {AlertCode}. Pending {Pending}; due {Due}; blocked {Blocked}; maximum attempts {MaximumAttempts}.",
                alert.Code,
                alert.Health.Pending,
                alert.Health.Due,
                alert.Health.Blocked,
                alert.Health.MaximumAttemptCount);
        }
        else
        {
            logger.LogWarning(
                "CARE aggregate monitor {AlertCode}. Pending {Pending}; due {Due}; blocked {Blocked}; maximum attempts {MaximumAttempts}.",
                alert.Code,
                alert.Health.Pending,
                alert.Health.Due,
                alert.Health.Blocked,
                alert.Health.MaximumAttemptCount);
        }

        return Task.CompletedTask;
    }
}

internal sealed class CareOperationsMonitor(
    ICareOperationsSource source,
    ICareOperationsAlertSink sink,
    ICareOperationsAlertQueue queue,
    TimeProvider? timeProvider = null)
{
    internal const string RetryScheduledCode = "care_retry_scheduled";
    internal const string RetryDueCode = "care_retry_due";
    internal const string OperatorActionRequiredCode = "care_operator_action_required";
    internal const string RecoveredCode = "care_recovered";

    private readonly SemaphoreSlim cycleLock = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal async Task<CareOperationsAggregateAlert?> PollOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await cycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var health = await source.RefreshAsync(cancellationToken).ConfigureAwait(false);
            CareOperationsPolicy.RequireHealth(health);
            var queued = await queue.ObserveAsync(
                    health, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (queued is null) return null;
            await sink.PublishAsync(queued.Alert, cancellationToken).ConfigureAwait(false);
            await queue.MarkDeliveredAsync(
                    queued.Sequence,
                    queued.RecordSha256,
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            return queued.Alert;
        }
        finally
        {
            cycleLock.Release();
        }
    }

}

internal sealed class CareOperationsMonitoringWorker(
    CareOperationsMonitor monitor,
    SimulationCareOperationsMonitorOptions options,
    ILogger<CareOperationsMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await monitor.PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning(
                    "The CARE aggregate monitor cycle failed closed; source and exception content were omitted.");
            }

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
