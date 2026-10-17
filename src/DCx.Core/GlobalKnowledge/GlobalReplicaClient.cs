using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DCx.Core.GlobalKnowledge;

public sealed partial class GlobalReplicaClient
{
    public const int TransferChunkBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    private readonly byte[] trustRoot;
    private readonly string releasesRoot;
    private readonly string catalogsRoot;
    private readonly string stagingRoot;
    private readonly string artifactsRoot;
    private readonly string activePath;
    private readonly string trustPolicyPath;
    private readonly SemaphoreSlim syncLock = new(1, 1);

    private GlobalReplicaClient(string replicaRoot, byte[] trustRoot)
    {
        ReplicaRoot = replicaRoot;
        this.trustRoot = trustRoot;
        releasesRoot = Path.Combine(replicaRoot, "releases");
        catalogsRoot = Path.Combine(replicaRoot, "catalogs");
        stagingRoot = Path.Combine(replicaRoot, "staging");
        artifactsRoot = Path.Combine(replicaRoot, "artifacts", "sha256");
        activePath = Path.Combine(replicaRoot, "active.json");
        trustPolicyPath = Path.Combine(replicaRoot, "trust-policy.json");
    }

    public string ReplicaRoot { get; }

    public static async Task<GlobalReplicaClient> OpenAsync(
        string applicationDataRoot,
        byte[] trustRootSubjectPublicKeyInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        ArgumentNullException.ThrowIfNull(trustRootSubjectPublicKeyInfo);
        if (trustRootSubjectPublicKeyInfo.Length is < 64 or > 1024)
        {
            throw new GlobalReplicaException("The global trust root has an invalid size.");
        }

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(trustRootSubjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != trustRootSubjectPublicKeyInfo.Length || verifier.KeySize != 256)
            {
                throw new GlobalReplicaException("The global trust root must be an exact P-256 public key.");
            }
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new GlobalReplicaException($"The global trust root is invalid: {exception.GetType().Name}.");
        }

        var trustId = GlobalReleaseCodec.Sha256(trustRootSubjectPublicKeyInfo);
        var root = Path.GetFullPath(Path.Combine(applicationDataRoot, "global", trustId));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GlobalReplicaException("The global replica root cannot be a reparse point.");
        }

        var client = new GlobalReplicaClient(root, trustRootSubjectPublicKeyInfo.ToArray());
        Directory.CreateDirectory(client.releasesRoot);
        Directory.CreateDirectory(client.catalogsRoot);
        Directory.CreateDirectory(client.stagingRoot);
        Directory.CreateDirectory(client.artifactsRoot);
        _ = await client.LoadActiveAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task<GlobalReplicaStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadActiveAsync(cancellationToken).ConfigureAwait(false);
        return new GlobalReplicaStatus(state.Sequence, state.ReleaseSha256, state.Resources.Count);
    }

    public async Task<GlobalTrustPolicyStatus?> GetTrustPolicyStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
        return state is null
            ? null
            : new GlobalTrustPolicyStatus(
                state.SignedPolicy.Policy.Revision,
                state.Sha256,
                state.SignedPolicy.Policy.ReleaseKeys.Count);
    }

    public async Task<GlobalTrustPolicyStatus> ApplyTrustPolicyAsync(
        ReadOnlyMemory<byte> policyEnvelope,
        CancellationToken cancellationToken = default)
    {
        if (policyEnvelope.IsEmpty || policyEnvelope.Length > GlobalTrustPolicyCodec.MaximumPolicyBytes)
        {
            throw new GlobalReplicaException("The proposed trust-policy envelope is empty or too large.");
        }

        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyTrustPolicyUnderLockAsync(policyEnvelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task<GlobalSyncResult> SynchronizeAsync(
        IGlobalRegistryTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trustPolicy = await SynchronizeTrustPolicyAsync(transport, cancellationToken).ConfigureAwait(false);
            var current = await LoadActiveAsync(trustPolicy, cancellationToken).ConfigureAwait(false);
            var latest = await transport.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                return new GlobalSyncResult(
                    new GlobalReplicaStatus(current.Sequence, current.ReleaseSha256, current.Resources.Count),
                    0,
                    0,
                    current.Sequence == 0);
            }

            ValidateDescriptor(latest);
            if (latest.Sequence < current.Sequence)
            {
                throw new GlobalReplicaException("The registry latest sequence is behind the active replica.");
            }

            if (latest.Sequence == current.Sequence)
            {
                if (!string.Equals(latest.Sha256, current.ReleaseSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GlobalReplicaException("The registry conflicts with the active release sequence.");
                }

                return new GlobalSyncResult(
                    new GlobalReplicaStatus(current.Sequence, current.ReleaseSha256, current.Resources.Count),
                    0,
                    0,
                    true);
            }

            var applied = 0;
            long bytesDownloaded = 0;
            for (var sequence = current.Sequence + 1; sequence <= latest.Sequence; sequence++)
            {
                var descriptor = await transport.GetReleaseAsync(sequence, cancellationToken).ConfigureAwait(false);
                ValidateDescriptor(descriptor);
                if (descriptor.Sequence != sequence)
                {
                    throw new GlobalReplicaException("The registry returned the wrong release sequence.");
                }

                var download = await DownloadReleaseAsync(transport, descriptor, cancellationToken).ConfigureAwait(false);
                bytesDownloaded += download.BytesDownloaded;
                var releaseBytes = await File.ReadAllBytesAsync(download.Path, cancellationToken).ConfigureAwait(false);
                var release = GlobalReleaseCodec.Decode(releaseBytes);
                VerifyRelease(release, trustPolicy);
                if (release.Manifest.Sequence != sequence ||
                    release.Manifest.PreviousSequence != current.Sequence ||
                    !string.Equals(
                        release.Manifest.PreviousReleaseSha256,
                        current.ReleaseSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new GlobalReplicaException("The signed release does not extend the active replica chain.");
                }

                var resources = GlobalCatalogReducer.Apply(current.Resources, release.Manifest);
                var catalogBytes = GlobalReleaseCodec.CreateCanonicalCatalog(resources);
                var releaseDestination = ReleasePath(sequence, descriptor.Sha256);
                var catalogDestination = CatalogPath(sequence, descriptor.Sha256);
                await WriteAtomicAsync(releaseDestination, releaseBytes, cancellationToken).ConfigureAwait(false);
                await WriteAtomicAsync(catalogDestination, catalogBytes, cancellationToken).ConfigureAwait(false);
                await WriteActiveAsync(new ActivePointer(sequence, descriptor.Sha256), cancellationToken).ConfigureAwait(false);
                File.Delete(download.Path);
                current = new ActiveState(sequence, descriptor.Sha256.ToLowerInvariant(), resources);
                applied++;
            }

            return new GlobalSyncResult(
                new GlobalReplicaStatus(current.Sequence, current.ReleaseSha256, current.Resources.Count),
                applied,
                bytesDownloaded,
                false);
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task<GlobalReplicaStatus> RollbackAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var matches = Directory.GetFiles(releasesRoot, $"{sequence:D12}-*.release.json");
            if (matches.Length != 1)
            {
                throw new GlobalReplicaException("The requested verified rollback release is unavailable or ambiguous.");
            }

            var fileName = Path.GetFileName(matches[0]);
            var digest = fileName.Substring(13, 64);
            var pointer = new ActivePointer(sequence, digest);
            var trustPolicy = await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
            var state = await LoadStateAsync(pointer, trustPolicy, cancellationToken).ConfigureAwait(false);
            await WriteActiveAsync(pointer, cancellationToken).ConfigureAwait(false);
            return new GlobalReplicaStatus(state.Sequence, state.ReleaseSha256, state.Resources.Count);
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task<IReadOnlyList<GlobalResourceRecord>> SearchAsync(
        string query,
        float[]? embedding,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50 || embedding is { Length: > GlobalReleaseCodec.MaximumEmbeddingDimensions } ||
            embedding?.Any(value => !float.IsFinite(value)) == true)
        {
            throw new ArgumentException("The global search limit or embedding is invalid.");
        }

        var state = await LoadActiveAsync(cancellationToken).ConfigureAwait(false);
        var terms = SearchTermRegex().Matches(query ?? string.Empty)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (terms.Length == 0 && embedding is null)
        {
            return [];
        }

        return state.Resources
            .Select(resource => new
            {
                Resource = resource,
                Score = Score(resource, terms, embedding),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Resource.ResourceId, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => item.Resource)
            .ToArray();
    }

    public async Task<string> GetArtifactAsync(
        string sha256,
        IGlobalRegistryTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!IsSha256(sha256))
        {
            throw new GlobalReplicaException("The requested global artifact digest is invalid.");
        }

        var state = await LoadActiveAsync(cancellationToken).ConfigureAwait(false);
        var reference = state.Resources.Select(item => item.Artifact)
            .FirstOrDefault(item => item is not null && item.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            ?? throw new GlobalReplicaException("The artifact is not referenced by the active signed catalog.");
        var normalized = sha256.ToLowerInvariant();
        var destinationDirectory = Path.Combine(artifactsRoot, normalized[..2]);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, normalized);
        if (File.Exists(destination))
        {
            await VerifyFileAsync(destination, normalized, reference.SizeBytes, cancellationToken).ConfigureAwait(false);
            return destination;
        }

        var partial = Path.Combine(stagingRoot, $"artifact-{normalized}.part");
        await DownloadChunksAsync(
            partial,
            reference.SizeBytes,
            (offset, count, token) => transport.DownloadArtifactChunkAsync(normalized, offset, count, token),
            cancellationToken).ConfigureAwait(false);
        await VerifyFileAsync(partial, normalized, reference.SizeBytes, cancellationToken).ConfigureAwait(false);
        File.Move(partial, destination, overwrite: false);
        return destination;
    }

    private async Task<ActiveState> LoadActiveAsync(CancellationToken cancellationToken)
    {
        var trustPolicy = await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
        return await LoadActiveAsync(trustPolicy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActiveState> LoadActiveAsync(
        TrustPolicyState? trustPolicy,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(activePath))
        {
            return new ActiveState(0, null, []);
        }

        try
        {
            var pointerBytes = await File.ReadAllBytesAsync(activePath, cancellationToken).ConfigureAwait(false);
            var pointer = JsonSerializer.Deserialize<ActivePointer>(pointerBytes, JsonOptions)
                ?? throw new GlobalReplicaException("The active global pointer is empty.");
            if (pointer.Sequence < 1 || !IsSha256(pointer.ReleaseSha256))
            {
                throw new GlobalReplicaException("The active global pointer is invalid.");
            }

            return await LoadStateAsync(pointer, trustPolicy, cancellationToken).ConfigureAwait(false);
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new GlobalReplicaException($"The active global replica is unreadable: {exception.GetType().Name}.");
        }
    }

    private async Task<ActiveState> LoadStateAsync(
        ActivePointer pointer,
        TrustPolicyState? trustPolicy,
        CancellationToken cancellationToken)
    {
        var releasePath = ReleasePath(pointer.Sequence, pointer.ReleaseSha256);
        var catalogPath = CatalogPath(pointer.Sequence, pointer.ReleaseSha256);
        await VerifyFileAsync(releasePath, pointer.ReleaseSha256, null, cancellationToken).ConfigureAwait(false);
        var releaseBytes = await File.ReadAllBytesAsync(releasePath, cancellationToken).ConfigureAwait(false);
        var release = GlobalReleaseCodec.Decode(releaseBytes);
        VerifyRelease(release, trustPolicy);
        if (release.Manifest.Sequence != pointer.Sequence)
        {
            throw new GlobalReplicaException("The active pointer and signed release sequence differ.");
        }

        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        if (!GlobalReleaseCodec.Sha256(catalogBytes).Equals(
                release.Manifest.ResultCatalogSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GlobalReplicaException("The active catalog failed its signed digest check.");
        }

        var resources = GlobalReleaseCodec.DecodeCatalog(catalogBytes);
        return new ActiveState(pointer.Sequence, pointer.ReleaseSha256.ToLowerInvariant(), resources);
    }

    private async Task<TrustPolicyState?> LoadTrustPolicyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(trustPolicyPath))
        {
            return null;
        }

        try
        {
            var information = new FileInfo(trustPolicyPath);
            if (information.Length is <= 0 or > GlobalTrustPolicyCodec.MaximumPolicyBytes)
            {
                throw new GlobalReplicaException("The persisted trust-policy envelope has an invalid size.");
            }

            var envelope = await File.ReadAllBytesAsync(trustPolicyPath, cancellationToken).ConfigureAwait(false);
            var signedPolicy = GlobalTrustPolicyCodec.Decode(envelope);
            GlobalTrustPolicyCodec.Verify(signedPolicy, trustRoot);
            return new TrustPolicyState(signedPolicy, GlobalReleaseCodec.Sha256(envelope));
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GlobalReplicaException($"The persisted trust policy is unreadable: {exception.GetType().Name}.");
        }
    }

    private async Task<GlobalTrustPolicyStatus> ApplyTrustPolicyUnderLockAsync(
        ReadOnlyMemory<byte> policyEnvelope,
        CancellationToken cancellationToken)
    {
        var proposed = GlobalTrustPolicyCodec.Decode(policyEnvelope.Span);
        GlobalTrustPolicyCodec.Verify(proposed, trustRoot);
        var proposedSha256 = GlobalReleaseCodec.Sha256(policyEnvelope.Span);
        var current = await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && proposed.Policy.Revision == current.SignedPolicy.Policy.Revision &&
            proposedSha256.Equals(current.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new GlobalTrustPolicyStatus(
                proposed.Policy.Revision,
                proposedSha256,
                proposed.Policy.ReleaseKeys.Count);
        }

        var expectedRevision = (current?.SignedPolicy.Policy.Revision ?? 0) + 1;
        var expectedPredecessor = current?.Sha256;
        if (proposed.Policy.Revision != expectedRevision ||
            !string.Equals(
                proposed.Policy.PreviousPolicySha256,
                expectedPredecessor,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GlobalReplicaException("The proposed trust policy does not extend the active policy chain.");
        }

        await WriteAtomicAsync(trustPolicyPath, policyEnvelope, cancellationToken).ConfigureAwait(false);
        return new GlobalTrustPolicyStatus(
            proposed.Policy.Revision,
            proposedSha256,
            proposed.Policy.ReleaseKeys.Count);
    }

    private async Task<TrustPolicyState?> SynchronizeTrustPolicyAsync(
        IGlobalRegistryTransport transport,
        CancellationToken cancellationToken)
    {
        var current = await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
        var latest = await transport.GetLatestTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return current;
        }

        ValidateTrustPolicyDescriptor(latest);
        var currentRevision = current?.SignedPolicy.Policy.Revision ?? 0;
        if (latest.Revision < currentRevision)
        {
            throw new GlobalReplicaException("The registry trust-policy revision is behind the local policy.");
        }

        if (latest.Revision == currentRevision)
        {
            if (!string.Equals(latest.Sha256, current?.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new GlobalReplicaException("The registry conflicts with the active trust-policy revision.");
            }

            return current;
        }

        for (var revision = currentRevision + 1; revision <= latest.Revision; revision++)
        {
            var descriptor = await transport.GetTrustPolicyAsync(revision, cancellationToken).ConfigureAwait(false);
            ValidateTrustPolicyDescriptor(descriptor);
            if (descriptor.Revision != revision)
            {
                throw new GlobalReplicaException("The registry returned the wrong trust-policy revision.");
            }

            var partial = Path.Combine(
                stagingRoot,
                $"trust-policy-{revision:D12}-{descriptor.Sha256.ToLowerInvariant()}.part");
            _ = await DownloadChunksAsync(
                    partial,
                    descriptor.SizeBytes,
                    (offset, count, token) =>
                        transport.DownloadTrustPolicyChunkAsync(revision, offset, count, token),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await VerifyFileAsync(partial, descriptor.Sha256, descriptor.SizeBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                File.Delete(partial);
                throw;
            }

            var envelope = await File.ReadAllBytesAsync(partial, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await ApplyTrustPolicyUnderLockAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(partial);
            }
        }

        return await LoadTrustPolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void VerifyRelease(SignedGlobalRelease release, TrustPolicyState? trustPolicy)
    {
        if (trustPolicy is null)
        {
            GlobalReleaseCodec.Verify(release, trustRoot);
            return;
        }

        var authorizedKeys = GlobalTrustPolicyCodec.AuthorizedReleaseKeys(
            trustPolicy.SignedPolicy.Policy,
            release.Manifest.Sequence);
        foreach (var authorizedKey in authorizedKeys)
        {
            try
            {
                GlobalReleaseCodec.Verify(release, authorizedKey);
                return;
            }
            catch (GlobalReplicaException)
            {
            }
        }

        throw new GlobalReplicaException("The global release was not signed by a key authorized for its sequence.");
    }

    private async Task<(string Path, long BytesDownloaded)> DownloadReleaseAsync(
        IGlobalRegistryTransport transport,
        GlobalReleaseDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var partial = Path.Combine(stagingRoot, $"release-{descriptor.Sequence:D12}-{descriptor.Sha256.ToLowerInvariant()}.part");
        var downloaded = await DownloadChunksAsync(
            partial,
            descriptor.SizeBytes,
            (offset, count, token) => transport.DownloadReleaseChunkAsync(descriptor.Sequence, offset, count, token),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyFileAsync(partial, descriptor.Sha256, descriptor.SizeBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return (partial, downloaded);
    }

    private static async Task<long> DownloadChunksAsync(
        string partialPath,
        long expectedBytes,
        Func<long, int, CancellationToken, Task<byte[]>> download,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is <= 0 or > 256L * 1024 * 1024)
        {
            throw new GlobalReplicaException("The registry object size is invalid.");
        }

        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > expectedBytes)
        {
            File.Delete(partialPath);
            existing = 0;
        }

        long downloaded = 0;
        await using var stream = new FileStream(
            partialPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            TransferChunkBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var offset = existing;
        while (offset < expectedBytes)
        {
            var count = (int)Math.Min(TransferChunkBytes, expectedBytes - offset);
            var chunk = await download(offset, count, cancellationToken).ConfigureAwait(false);
            if (chunk.Length is 0 || chunk.Length > count || offset + chunk.Length > expectedBytes)
            {
                throw new GlobalReplicaException("The registry returned an invalid transfer chunk.");
            }

            await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            offset += chunk.Length;
            downloaded += chunk.Length;
        }

        return downloaded;
    }

    private async Task WriteActiveAsync(ActivePointer pointer, CancellationToken cancellationToken) =>
        await WriteAtomicAsync(activePath, JsonSerializer.SerializeToUtf8Bytes(pointer, JsonOptions), cancellationToken)
            .ConfigureAwait(false);

    private static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task VerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new GlobalReplicaException("A required global replica file is missing.");
        }

        var information = new FileInfo(path);
        if (expectedBytes is not null && information.Length != expectedBytes)
        {
            throw new GlobalReplicaException("A global replica file has an unexpected size.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(expectedSha256)))
        {
            throw new GlobalReplicaException("A global replica file failed its SHA-256 check.");
        }
    }

    private static void ValidateDescriptor(GlobalReleaseDescriptor descriptor)
    {
        if (descriptor.Sequence < 1 || !IsSha256(descriptor.Sha256) ||
            descriptor.SizeBytes is <= 0 or > GlobalReleaseCodec.MaximumReleaseBytes)
        {
            throw new GlobalReplicaException("The registry release descriptor is invalid.");
        }
    }

    private static void ValidateTrustPolicyDescriptor(GlobalTrustPolicyDescriptor descriptor)
    {
        if (descriptor.Revision < 1 || !IsSha256(descriptor.Sha256) ||
            descriptor.SizeBytes is <= 0 or > GlobalTrustPolicyCodec.MaximumPolicyBytes)
        {
            throw new GlobalReplicaException("The registry trust-policy descriptor is invalid.");
        }
    }

    private string ReleasePath(long sequence, string digest) =>
        Path.Combine(releasesRoot, $"{sequence:D12}-{digest.ToLowerInvariant()}.release.json");

    private string CatalogPath(long sequence, string digest) =>
        Path.Combine(catalogsRoot, $"{sequence:D12}-{digest.ToLowerInvariant()}.catalog.json");

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static double Score(GlobalResourceRecord resource, string[] terms, float[]? embedding)
    {
        var searchable = $"{resource.Title}\n{resource.Summary}\n{string.Join(' ', resource.Capabilities)}";
        var text = terms.Length == 0
            ? 0
            : terms.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase)) / (double)terms.Length;
        var vector = embedding is not null && resource.Embedding?.Length == embedding.Length
            ? (Cosine(embedding, resource.Embedding) + 1d) / 2d
            : 0;
        if (text > 0 && vector > 0)
        {
            return (text * 0.55) + (vector * 0.45);
        }

        return Math.Max(text, vector);
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude == 0 || rightMagnitude == 0
            ? 0
            : dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_-]{2,64}", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();

    private sealed record ActivePointer(long Sequence, string ReleaseSha256);

    private sealed record TrustPolicyState(SignedGlobalTrustPolicy SignedPolicy, string Sha256);

    private sealed record ActiveState(
        long Sequence,
        string? ReleaseSha256,
        IReadOnlyList<GlobalResourceRecord> Resources);
}
