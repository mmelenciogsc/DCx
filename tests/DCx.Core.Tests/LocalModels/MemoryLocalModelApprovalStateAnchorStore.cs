using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

internal sealed class MemoryLocalModelApprovalStateAnchorStore : ILocalModelApprovalStateAnchorStore
{
    private readonly Dictionary<(string Kind, string Storage), LocalModelApprovalStateAnchor> anchors = [];

    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public bool FailDeletes { get; set; }

    public Task<LocalModelApprovalStateAnchor?> ReadAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailReads) throw new InvalidOperationException("The protected approval anchor cannot be read.");
        return Task.FromResult(anchors.TryGetValue((stateKind, storageId), out var anchor)
            ? anchor
            : null);
    }

    public Task WriteAsync(
        LocalModelApprovalStateAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) throw new InvalidOperationException("The protected approval anchor cannot be written.");
        anchors[(anchor.StateKind, anchor.StorageId)] = anchor;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailDeletes) throw new InvalidOperationException("The protected approval anchor cannot be cleared.");
        anchors.Remove((stateKind, storageId));
        return Task.CompletedTask;
    }

    public void RemoveAll() => anchors.Clear();

    public IReadOnlyCollection<LocalModelApprovalStateAnchor> Values => anchors.Values;
}
