namespace DCx.Core.GlobalKnowledge;

public sealed record GlobalKnowledgeViewStatus(
    bool IsConfigured,
    string StatusCode,
    GlobalReplicaStatus? Replica,
    GlobalTrustPolicyStatus? TrustPolicy,
    bool CanSynchronize);

public interface IGlobalKnowledgeViewSource
{
    Task<GlobalKnowledgeViewStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GlobalResourceRecord>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<GlobalSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
}

public sealed class GlobalReplicaViewSource : IGlobalKnowledgeViewSource
{
    private readonly GlobalReplicaClient client;
    private readonly IGlobalRegistryTransport? transport;

    public GlobalReplicaViewSource(
        GlobalReplicaClient client,
        IGlobalRegistryTransport? transport = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.transport = transport;
    }

    public async Task<GlobalKnowledgeViewStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var replica = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var policy = await client.GetTrustPolicyStatusAsync(cancellationToken).ConfigureAwait(false);
        return new GlobalKnowledgeViewStatus(
            true,
            replica.Sequence == 0 ? "replica_empty" : "replica_ready",
            replica,
            policy,
            transport is not null);
    }

    public Task<IReadOnlyList<GlobalResourceRecord>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        client.SearchAsync(query, null, limit, cancellationToken);

    public Task<GlobalSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        transport is null
            ? Task.FromException<GlobalSyncResult>(
                new GlobalReplicaException("Global synchronization is not configured."))
            : client.SynchronizeAsync(transport, cancellationToken);
}

public sealed class UnavailableGlobalKnowledgeViewSource : IGlobalKnowledgeViewSource
{
    public Task<GlobalKnowledgeViewStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GlobalKnowledgeViewStatus(
            false,
            "not_configured",
            null,
            null,
            false));
    }

    public Task<IReadOnlyList<GlobalResourceRecord>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<GlobalResourceRecord>>([]);
    }

    public Task<GlobalSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<GlobalSyncResult>(
            new GlobalReplicaException("Global synchronization is not configured."));
    }
}
