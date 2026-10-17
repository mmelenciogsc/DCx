namespace DCx.Core.GlobalKnowledge;

public static class GlobalCatalogReducer
{
    public static IReadOnlyList<GlobalResourceRecord> Apply(
        IEnumerable<GlobalResourceRecord> current,
        GlobalReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(current);
        GlobalReleaseCodec.Validate(manifest);
        var result = ApplyChanges(current, manifest.Changes);
        var resultDigest = GlobalReleaseCodec.Sha256(GlobalReleaseCodec.CreateCanonicalCatalog(result));
        if (!string.Equals(resultDigest, manifest.ResultCatalogSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new GlobalReplicaException("The applied global catalog does not match the signed result digest.");
        }

        return result;
    }

    public static IReadOnlyList<GlobalResourceRecord> ApplyChanges(
        IEnumerable<GlobalResourceRecord> current,
        IReadOnlyList<GlobalReleaseChange> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var resources = current.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            resources.TryGetValue(change.ResourceId, out var existing);
            switch (change.Kind)
            {
                case GlobalReleaseChangeKind.Upsert:
                    if (existing is not null && string.Equals(existing.Version, change.Version, StringComparison.Ordinal))
                    {
                        throw new GlobalReplicaException("A release cannot replace a resource without changing its version.");
                    }

                    resources[change.ResourceId] = change.Resource!;
                    break;
                case GlobalReleaseChangeKind.Deprecate:
                    if (existing is null || existing.Version != change.Version || existing.Deprecated)
                    {
                        throw new GlobalReplicaException("A release deprecation does not match an active resource version.");
                    }

                    resources[change.ResourceId] = existing with { Deprecated = true };
                    break;
                case GlobalReleaseChangeKind.Remove:
                    if (existing is null || existing.Version != change.Version)
                    {
                        throw new GlobalReplicaException("A release removal does not match an active resource version.");
                    }

                    resources.Remove(change.ResourceId);
                    break;
                default:
                    throw new GlobalReplicaException("The global release change kind is unsupported.");
            }
        }

        return resources.Values.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToArray();
    }
}
