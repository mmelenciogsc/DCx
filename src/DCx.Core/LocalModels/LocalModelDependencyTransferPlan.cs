using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public sealed record LocalModelDependencyTransferItem(
    Uri SourceUri,
    long ExpectedBytes,
    string Sha256,
    IReadOnlyList<string> Roles);

public sealed record LocalModelDependencyTransferPlan(
    int SchemaVersion,
    string CatalogId,
    string CatalogSha256,
    string PlanSha256,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<LocalModelDependencyTransferItem> Items,
    long TransferBytes,
    long MinimumFreeBytes,
    long? ObservedAvailableFreeBytes,
    bool CapacitySufficient);

/// <summary>
/// Produces a read-only, deterministic footprint for a future dependency transfer.
/// It performs no network, filesystem mutation, installation, extraction, pull, or execution.
/// </summary>
public sealed class LocalModelDependencyTransferPlanner
{
    public const int CurrentSchemaVersion = 1;
    public const long WorkspaceReserveBytes = 512L * 1024 * 1024;
    public const int TransferAndRetainedCopyCount = 2;

    public LocalModelDependencyTransferPlan CreateForAll(
        LocalModelDependencyCatalogReview catalog,
        long? observedAvailableFreeBytes) =>
        Create(catalog, catalog?.Candidates.Select(candidate => candidate.Id)
            ?? throw new ArgumentNullException(nameof(catalog)), observedAvailableFreeBytes);

    public LocalModelDependencyTransferPlan Create(
        LocalModelDependencyCatalogReview catalog,
        IEnumerable<string> selectedCandidateIds,
        long? observedAvailableFreeBytes)
    {
        ValidateCatalog(catalog);
        ArgumentNullException.ThrowIfNull(selectedCandidateIds);
        if (observedAvailableFreeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAvailableFreeBytes),
                "Observed free storage cannot be negative.");
        }

        var candidateById = catalog.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var selectedIds = selectedCandidateIds.ToArray();
        if (selectedIds.Length is < 1 or > 16)
        {
            throw new InvalidOperationException("The dependency transfer selection is empty or too large.");
        }
        var uniqueSelectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in selectedIds)
        {
            LocalModelDependencyCatalogLoader.ValidateIdentifier(id, "transfer candidate");
            if (!uniqueSelectedIds.Add(id))
            {
                throw new InvalidOperationException("The dependency transfer selection contains a duplicate candidate.");
            }
            if (!candidateById.ContainsKey(id))
            {
                throw new InvalidOperationException("The dependency transfer selection is not present in the authenticated catalog.");
            }
        }

        var sortedIds = uniqueSelectedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var mutableItems = new Dictionary<string, MutableTransferItem>(StringComparer.Ordinal);
        foreach (var id in sortedIds)
        {
            var candidate = candidateById[id];
            AddItem(
                mutableItems,
                candidate.SourceUri,
                candidate.ExpectedBytes,
                candidate.Sha256,
                $"candidate/{candidate.Id}");

            AddItem(
                mutableItems,
                candidate.License.SourceUri,
                candidate.License.ExpectedBytes,
                candidate.License.Sha256,
                $"license/{candidate.Id}");

            if (candidate.Kind != LocalModelDependencyKind.OllamaManifest) continue;
            foreach (var layer in candidate.ManagedLayers)
            {
                AddItem(
                    mutableItems,
                    CreateOllamaLayerUri(candidate.SourceUri, layer.Sha256),
                    layer.ExpectedBytes,
                    layer.Sha256,
                    $"ollama-layer/{candidate.Id}/{layer.MediaType}");
            }
        }

        var items = mutableItems.Values
            .OrderBy(item => item.SourceUri.AbsoluteUri, StringComparer.Ordinal)
            .Select(item => new LocalModelDependencyTransferItem(
                item.SourceUri,
                item.ExpectedBytes,
                item.Sha256,
                Array.AsReadOnly(item.Roles.OrderBy(role => role, StringComparer.Ordinal).ToArray())))
            .ToArray();

        long transferBytes = 0;
        foreach (var item in items) transferBytes = checked(transferBytes + item.ExpectedBytes);
        var minimumFreeBytes = checked(
            checked(transferBytes * TransferAndRetainedCopyCount) + WorkspaceReserveBytes);
        var planSha256 = ComputePlanSha256(
            catalog.CatalogId,
            catalog.CatalogSha256,
            sortedIds,
            items,
            transferBytes,
            minimumFreeBytes);

        return new LocalModelDependencyTransferPlan(
            CurrentSchemaVersion,
            catalog.CatalogId,
            catalog.CatalogSha256,
            planSha256,
            Array.AsReadOnly(sortedIds),
            Array.AsReadOnly(items),
            transferBytes,
            minimumFreeBytes,
            observedAvailableFreeBytes,
            observedAvailableFreeBytes >= minimumFreeBytes);
    }

    public static long? TryGetAvailableFreeBytes(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var canonical = Path.GetFullPath(path);
            var root = Path.GetPathRoot(canonical);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var value = new DriveInfo(root).AvailableFreeSpace;
            return value < 0 ? null : value;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    internal static void ValidateForTransfer(LocalModelDependencyTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("The dependency transfer plan schema is unsupported.");
        }
        LocalModelDependencyCatalogLoader.ValidateIdentifier(plan.CatalogId, "transfer catalog");
        LocalModelDependencyCatalogLoader.ValidateSha256(plan.CatalogSha256, "transfer catalog");
        LocalModelDependencyCatalogLoader.ValidateSha256(plan.PlanSha256, "transfer plan");
        if (plan.CandidateIds is null || plan.CandidateIds.Count is < 1 or > 16 ||
            plan.Items is null || plan.Items.Count is < 1 or > 64)
        {
            throw new InvalidOperationException("The dependency transfer plan shape is invalid.");
        }

        var previousCandidate = string.Empty;
        foreach (var candidateId in plan.CandidateIds)
        {
            LocalModelDependencyCatalogLoader.ValidateIdentifier(candidateId, "transfer candidate");
            if (previousCandidate.Length > 0 &&
                string.CompareOrdinal(previousCandidate, candidateId) >= 0)
            {
                throw new InvalidOperationException("The dependency transfer candidates are not uniquely ordered.");
            }
            previousCandidate = candidateId;
        }

        long transferBytes = 0;
        var previousSource = string.Empty;
        foreach (var item in plan.Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            LocalModelDependencyCatalogLoader.ValidateUri(item.SourceUri, "transfer item");
            LocalModelDependencyCatalogLoader.ValidateSha256(item.Sha256, "transfer item");
            if (item.ExpectedBytes is < 1 or > 1024L * 1024 * 1024 ||
                item.Roles is null || item.Roles.Count is < 1 or > 32)
            {
                throw new InvalidOperationException("The dependency transfer item is invalid.");
            }
            var source = item.SourceUri.AbsoluteUri;
            if (previousSource.Length > 0 && string.CompareOrdinal(previousSource, source) >= 0)
            {
                throw new InvalidOperationException("The dependency transfer items are not uniquely ordered.");
            }
            previousSource = source;

            var previousRole = string.Empty;
            foreach (var role in item.Roles)
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length > 320 ||
                    role != role.Trim() || role.Any(char.IsControl) ||
                    (previousRole.Length > 0 && string.CompareOrdinal(previousRole, role) >= 0))
                {
                    throw new InvalidOperationException("The dependency transfer item roles are invalid.");
                }
                previousRole = role;
            }
            transferBytes = checked(transferBytes + item.ExpectedBytes);
        }

        var minimumFreeBytes = checked(
            checked(transferBytes * TransferAndRetainedCopyCount) + WorkspaceReserveBytes);
        if (plan.TransferBytes != transferBytes || plan.MinimumFreeBytes != minimumFreeBytes ||
            plan.ObservedAvailableFreeBytes is < 0 ||
            plan.CapacitySufficient != (plan.ObservedAvailableFreeBytes >= minimumFreeBytes))
        {
            throw new InvalidOperationException("The dependency transfer plan totals or capacity observation are invalid.");
        }

        var actualPlanSha256 = ComputePlanSha256(
            plan.CatalogId,
            plan.CatalogSha256,
            plan.CandidateIds,
            plan.Items,
            transferBytes,
            minimumFreeBytes);
        LocalModelDependencyCatalogLoader.RequireDigest(
            actualPlanSha256,
            plan.PlanSha256,
            "dependency transfer plan");
    }

    private static void ValidateCatalog(LocalModelDependencyCatalogReview catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != LocalModelDependencyCatalogLoader.CurrentSchemaVersion)
        {
            throw new InvalidOperationException("The dependency transfer catalog schema is unsupported.");
        }
        LocalModelDependencyCatalogLoader.ValidateIdentifier(catalog.CatalogId, "transfer catalog");
        LocalModelDependencyCatalogLoader.ValidateSha256(catalog.CatalogSha256, "transfer catalog");
        if (catalog.Candidates is null || catalog.Candidates.Count is < 1 or > 16)
        {
            throw new InvalidOperationException("The dependency transfer catalog candidate list is invalid.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in catalog.Candidates)
        {
            LocalModelDependencyCatalogLoader.ValidateCandidate(candidate);
            if (!ids.Add(candidate.Id))
            {
                throw new InvalidOperationException("The dependency transfer catalog contains a duplicate candidate.");
            }
        }
    }

    private static void AddItem(
        IDictionary<string, MutableTransferItem> items,
        Uri sourceUri,
        long expectedBytes,
        string sha256,
        string role)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        LocalModelDependencyCatalogLoader.ValidateSha256(sha256, "transfer item");
        if (expectedBytes <= 0 || role.Length is < 1 or > 320 || role.Any(char.IsControl))
        {
            throw new InvalidOperationException("The dependency transfer item is invalid.");
        }

        var key = sourceUri.AbsoluteUri;
        if (items.TryGetValue(key, out var existing))
        {
            if (existing.ExpectedBytes != expectedBytes ||
                !string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("One dependency source has conflicting transfer identities.");
            }
            if (!existing.Roles.Add(role))
            {
                throw new InvalidOperationException("The dependency transfer item role is duplicated.");
            }
            return;
        }

        items.Add(key, new MutableTransferItem(
            sourceUri,
            expectedBytes,
            sha256,
            new HashSet<string>(StringComparer.Ordinal) { role }));
    }

    private static Uri CreateOllamaLayerUri(Uri manifestUri, string layerSha256)
    {
        LocalModelDependencyCatalogLoader.ValidateSha256(layerSha256, "managed layer");
        var marker = "/manifests/";
        var markerIndex = manifestUri.AbsolutePath.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            throw new InvalidOperationException("The Ollama manifest source cannot derive a managed-layer source.");
        }
        var repositoryPath = manifestUri.AbsolutePath[..markerIndex];
        return new UriBuilder(manifestUri.Scheme, manifestUri.IdnHost)
        {
            Path = $"{repositoryPath}/blobs/sha256:{layerSha256}",
        }.Uri;
    }

    private static string ComputePlanSha256(
        string catalogId,
        string catalogSha256,
        IReadOnlyList<string> candidateIds,
        IReadOnlyList<LocalModelDependencyTransferItem> items,
        long transferBytes,
        long minimumFreeBytes)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddString(digest, "DCx/local-model-dependency-transfer-plan/v1");
        AddString(digest, catalogId);
        AddString(digest, catalogSha256);
        AddInt32(digest, candidateIds.Count);
        foreach (var id in candidateIds) AddString(digest, id);
        AddInt32(digest, items.Count);
        foreach (var item in items)
        {
            AddString(digest, item.SourceUri.AbsoluteUri);
            AddInt64(digest, item.ExpectedBytes);
            AddString(digest, item.Sha256);
            AddInt32(digest, item.Roles.Count);
            foreach (var role in item.Roles) AddString(digest, role);
        }
        AddInt64(digest, transferBytes);
        AddInt64(digest, minimumFreeBytes);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void AddString(IncrementalHash digest, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            AddInt32(digest, bytes.Length);
            digest.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AddInt32(IncrementalHash digest, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        digest.AppendData(bytes);
    }

    private static void AddInt64(IncrementalHash digest, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        digest.AppendData(bytes);
    }

    private sealed record MutableTransferItem(
        Uri SourceUri,
        long ExpectedBytes,
        string Sha256,
        HashSet<string> Roles);
}
