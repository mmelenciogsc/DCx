namespace DCx.GlobalService;

internal enum ContributionPublicationCheckpoint
{
    IntentStored = 1,
    RegistryCommitted = 2,
}

internal interface IContributionPublicationFaultInjector
{
    Task OnCheckpointAsync(
        ContributionPublicationCheckpoint checkpoint,
        string operationId,
        CancellationToken cancellationToken);
}
