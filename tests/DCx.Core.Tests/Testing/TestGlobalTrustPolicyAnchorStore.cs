using System.Collections.Concurrent;
using DCx.Core.GlobalKnowledge;

namespace DCx.Core.Tests.Testing;

internal sealed class TestGlobalTrustPolicyAnchorStore : IGlobalTrustPolicyAnchorStore
{
    private readonly ConcurrentDictionary<string, GlobalTrustPolicyAnchor> anchors = new(StringComparer.OrdinalIgnoreCase);
    private int failNextAdvance;

    public static TestGlobalTrustPolicyAnchorStore Shared { get; } = new();

    public bool FailNextAdvance
    {
        set => Interlocked.Exchange(ref failNextAdvance, value ? 1 : 0);
    }

    public Task<GlobalTrustPolicyAnchor?> ReadAsync(
        string trustRootSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        anchors.TryGetValue(trustRootSha256, out var anchor);
        return Task.FromResult(anchor);
    }

    public Task AdvanceAsync(
        GlobalTrustPolicyAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref failNextAdvance, 0) == 1)
        {
            throw new GlobalReplicaException("Synthetic protected-anchor write failure.");
        }

        anchors.AddOrUpdate(
            anchor.TrustRootSha256,
            anchor,
            (_, current) => anchor.Revision > current.Revision || anchor == current
                ? anchor
                : throw new GlobalReplicaException("Synthetic protected-anchor rollback or conflict."));
        return Task.CompletedTask;
    }

    public void Set(GlobalTrustPolicyAnchor anchor) => anchors[anchor.TrustRootSha256] = anchor;

    public GlobalTrustPolicyAnchor? Get(string trustRootSha256) =>
        anchors.TryGetValue(trustRootSha256, out var anchor) ? anchor : null;
}
