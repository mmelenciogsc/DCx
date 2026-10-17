namespace DCx.GlobalService;

internal sealed record SimulationContributionWorkerOptions(TimeSpan PollInterval)
{
    public static SimulationContributionWorkerOptions? Load(bool contributionApiEnabled)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DCX_GLOBAL_CONTRIBUTION_WORKER"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!contributionApiEnabled)
        {
            throw new InvalidOperationException(
                "The contribution worker cannot run while the contribution API is disabled.");
        }

        var intervalText = Environment.GetEnvironmentVariable(
            "DCX_GLOBAL_CONTRIBUTION_WORKER_INTERVAL_MS");
        var intervalMilliseconds = 1000;
        if (intervalText is not null &&
            (!int.TryParse(intervalText, out intervalMilliseconds) ||
             intervalMilliseconds is < 100 or > 60_000))
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CONTRIBUTION_WORKER_INTERVAL_MS must be an integer from 100 through 60000.");
        }

        return new SimulationContributionWorkerOptions(
            TimeSpan.FromMilliseconds(intervalMilliseconds));
    }
}

internal sealed class ContributionPublicationWorker(
    ContributionGovernanceService governance,
    SimulationContributionWorkerOptions options,
    ILogger<ContributionPublicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await governance.ProcessReadyPublicationIntentsAsync(
                    DateTimeOffset.UtcNow,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning(
                    "The contribution publication worker cycle failed closed; request and exception content were omitted.");
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
