using System.Security.Cryptography;
using System.Text;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class GlobalReplicaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-p09-{Guid.NewGuid():N}");

    [Fact]
    public async Task TwoClients_UpdateSearchLazyDownloadRollback_WithoutChangingPrivateStore()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var repository = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "server"),
            key.ExportPkcs8PrivateKey());
        var artifactBytes = Enumerable.Range(0, 150_000).Select(index => (byte)(index % 251)).ToArray();
        var artifact = await repository.AddArtifactAsync(artifactBytes, "application/vnd.dcx.fixture");
        var browser = Resource(
            "global:browser-guide",
            "1.0.0",
            "Accessible browser guide",
            "Keyboard focus restoration and screen reader navigation.",
            [1f, 0f],
            artifact);
        var first = await repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, browser.ResourceId, browser.Version, browser)],
            DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        var transport = new RepositoryRegistryTransport(repository);
        var clientARoot = Path.Combine(root, "client-a");
        var privateStore = await PrivateKnowledgeStore.OpenAsync(clientARoot, "owner");
        await privateStore.PutAsync(new KnowledgeResourceDraft(
            "private:sentinel", "1", KnowledgeResourceKind.Ckb, "Private sentinel", "Never enters global sync.",
            "urn:dcx:private", "Private", [], []), null);
        var clientA = await GlobalReplicaClient.OpenAsync(clientARoot, repository.TrustRootSubjectPublicKeyInfo);
        var clientB = await GlobalReplicaClient.OpenAsync(Path.Combine(root, "client-b"), repository.TrustRootSubjectPublicKeyInfo);

        var syncA = await clientA.SynchronizeAsync(transport);
        var syncB = await clientB.SynchronizeAsync(transport);

        Assert.Equal(first.Sha256, syncA.Status.ReleaseSha256);
        Assert.Equal(first.Sha256, syncB.Status.ReleaseSha256);
        Assert.Equal("global:browser-guide", Assert.Single(await clientA.SearchAsync("keyboard screen reader", [1f, 0f])).ResourceId);
        Assert.Empty(Directory.GetFiles(Path.Combine(clientA.ReplicaRoot, "artifacts"), "*", SearchOption.AllDirectories));
        var artifactPath = await clientA.GetArtifactAsync(artifact.Sha256, transport);
        Assert.Equal(artifactBytes, await File.ReadAllBytesAsync(artifactPath));
        Assert.NotNull(await privateStore.GetAsync("private:sentinel"));

        var second = await repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Deprecate, browser.ResourceId, browser.Version)],
            DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        var updated = await clientA.SynchronizeAsync(transport);
        Assert.Equal(second.Sha256, updated.Status.ReleaseSha256);
        Assert.True(Assert.Single(await clientA.SearchAsync("accessible browser", null)).Deprecated);

        var rolledBack = await clientA.RollbackAsync(1);
        Assert.Equal(first.Sha256, rolledBack.ReleaseSha256);
        Assert.False(Assert.Single(await clientA.SearchAsync("accessible browser", null)).Deprecated);
        Assert.NotNull(await privateStore.GetAsync("private:sentinel"));
    }

    [Fact]
    public async Task Sync_RejectsReleaseSignedByUntrustedKeyWithoutActivation()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var resource = Resource("global:fixture", "1", "Fixture", "Signed by the wrong key.", [1f]);
        var changes = new[] { new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource) };
        var catalog = GlobalCatalogReducer.ApplyChanges([], changes);
        var manifest = new GlobalReleaseManifest(
            1,
            1,
            0,
            null,
            GlobalReleaseCodec.Sha256(GlobalReleaseCodec.CreateCanonicalCatalog(catalog)),
            DateTimeOffset.Parse("2026-10-17T00:00:00Z"),
            changes);
        var envelope = GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(manifest, attacker));
        var transport = new MemoryTransport(envelope);
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "client"),
            trusted.ExportSubjectPublicKeyInfo());

        await Assert.ThrowsAsync<GlobalReplicaException>(() => client.SynchronizeAsync(transport));

        Assert.Equal(0, (await client.GetStatusAsync()).Sequence);
        Assert.False(File.Exists(Path.Combine(client.ReplicaRoot, "active.json")));
    }

    [Fact]
    public async Task TrustPolicy_RotatesRevokesRejectsRollbackAndPreservesActiveReplica()
    {
        using var trustRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var oldReleaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextReleaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldAuthorization = Authorization(oldReleaseKey, 1);
        var nextAuthorization = Authorization(nextReleaseKey, 2);
        var policyOne = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1,
                1,
                null,
                DateTimeOffset.Parse("2026-10-17T00:00:00Z"),
                [oldAuthorization, nextAuthorization]),
            trustRoot));
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "rotating-client"),
            trustRoot.ExportSubjectPublicKeyInfo());

        var firstStatus = await client.ApplyTrustPolicyAsync(policyOne);
        Assert.Equal(firstStatus, await client.ApplyTrustPolicyAsync(policyOne));

        var firstResource = Resource("global:rotation-one", "1", "First signer", "Signed before rotation.", [1f]);
        var firstChanges = new[]
        {
            new GlobalReleaseChange(
                GlobalReleaseChangeKind.Upsert,
                firstResource.ResourceId,
                firstResource.Version,
                firstResource),
        };
        var firstCatalog = GlobalCatalogReducer.ApplyChanges([], firstChanges);
        var firstEnvelope = EncodeRelease(1, null, firstChanges, firstCatalog, oldReleaseKey);
        await client.SynchronizeAsync(new ChainMemoryTransport(firstEnvelope));

        var revokedOldAuthorization = oldAuthorization with { RevokedFromSequence = 2 };
        var policyTwoModel = new GlobalTrustPolicy(
            1,
            2,
            GlobalReleaseCodec.Sha256(policyOne),
            DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
            [revokedOldAuthorization, nextAuthorization]);
        var attackerPolicyTwo = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(policyTwoModel, attacker));
        await Assert.ThrowsAsync<GlobalReplicaException>(() => client.ApplyTrustPolicyAsync(attackerPolicyTwo));
        Assert.Equal(1, (await client.GetTrustPolicyStatusAsync())!.Revision);

        var policyTwo = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(policyTwoModel, trustRoot));
        Assert.Equal(2, (await client.ApplyTrustPolicyAsync(policyTwo)).Revision);
        await Assert.ThrowsAsync<GlobalReplicaException>(() => client.ApplyTrustPolicyAsync(policyOne));

        var secondResource = Resource("global:rotation-two", "1", "Second signer", "Signed after rotation.", [0f, 1f]);
        var secondChanges = new[]
        {
            new GlobalReleaseChange(
                GlobalReleaseChangeKind.Upsert,
                secondResource.ResourceId,
                secondResource.Version,
                secondResource),
        };
        var secondCatalog = GlobalCatalogReducer.ApplyChanges(firstCatalog, secondChanges);
        var maliciousSecondEnvelope = EncodeRelease(
            2,
            GlobalReleaseCodec.Sha256(firstEnvelope),
            secondChanges,
            secondCatalog,
            oldReleaseKey);
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            client.SynchronizeAsync(new ChainMemoryTransport(firstEnvelope, maliciousSecondEnvelope)));
        Assert.Equal(1, (await client.GetStatusAsync()).Sequence);

        var validSecondEnvelope = EncodeRelease(
            2,
            GlobalReleaseCodec.Sha256(firstEnvelope),
            secondChanges,
            secondCatalog,
            nextReleaseKey);
        Assert.Equal(2, (await client.SynchronizeAsync(
            new ChainMemoryTransport(firstEnvelope, validSecondEnvelope))).Status.Sequence);

        var policyThree = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1,
                3,
                GlobalReleaseCodec.Sha256(policyTwo),
                DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
                [revokedOldAuthorization with { RevokedFromSequence = 1 }, nextAuthorization]),
            trustRoot));
        await client.ApplyTrustPolicyAsync(policyThree);

        await Assert.ThrowsAsync<GlobalReplicaException>(() => client.RollbackAsync(1));
        Assert.Equal(2, (await client.GetStatusAsync()).Sequence);
        var reopened = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "rotating-client"),
            trustRoot.ExportSubjectPublicKeyInfo());
        Assert.Equal(3, (await reopened.GetTrustPolicyStatusAsync())!.Revision);
        Assert.Equal(2, (await reopened.GetStatusAsync()).Sequence);
    }

    [Fact]
    public async Task Sync_ResumesInterruptedReleaseFromCheckpoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var repository = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "server"),
            key.ExportPkcs8PrivateKey());
        var changes = Enumerable.Range(0, 300)
            .Select(index =>
            {
                var resource = Resource(
                    $"global:resource-{index:D4}",
                    "1",
                    $"Resource {index}",
                    $"{new string('x', 400)} {index}",
                    [index, 1f]);
                return new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource);
            })
            .ToArray();
        await repository.PublishAsync(changes, DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        var transport = new InterruptOnceTransport(new RepositoryRegistryTransport(repository));
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "client"),
            repository.TrustRootSubjectPublicKeyInfo);

        await Assert.ThrowsAsync<IOException>(() => client.SynchronizeAsync(transport));
        var result = await client.SynchronizeAsync(transport);

        Assert.Equal(1, result.Status.Sequence);
        Assert.Equal(300, result.Status.ResourceCount);
        Assert.Contains(transport.Offsets, offset => offset >= GlobalReplicaClient.TransferChunkBytes);
        Assert.True(result.BytesDownloaded < (await repository.GetLatestAsync())!.SizeBytes);
    }

    [Fact]
    public async Task Registry_RejectsVersionConflictAndDuplicateDelta()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var repository = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "server"),
            key.ExportPkcs8PrivateKey());
        var resource = Resource("global:fixture", "1", "Fixture", "First version.", [1f]);
        await repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<GlobalReplicaException>(() => repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
            DateTimeOffset.UtcNow));
        await Assert.ThrowsAnyAsync<Exception>(() => repository.PublishAsync(
            [
                new GlobalReleaseChange(GlobalReleaseChangeKind.Deprecate, resource.ResourceId, resource.Version),
                new GlobalReleaseChange(GlobalReleaseChangeKind.Remove, resource.ResourceId, resource.Version),
            ],
            DateTimeOffset.UtcNow));
        var absentArtifact = new GlobalArtifactReference(new string('a', 64), 10, "application/octet-stream");
        var artifactResource = Resource("global:artifact", "1", "Missing artifact", "Must fail closed.", [1f], absentArtifact);
        await Assert.ThrowsAsync<GlobalReplicaException>(() => repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, artifactResource.ResourceId, artifactResource.Version, artifactResource)],
            DateTimeOffset.UtcNow));
        Assert.Equal(1, (await repository.GetLatestAsync())!.Sequence);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalResourceRecord Resource(
        string id,
        string version,
        string title,
        string summary,
        float[] embedding,
        GlobalArtifactReference? artifact = null) =>
        new(id, version, KnowledgeResourceKind.Ckb, title, summary, "https://example.test/source", "CC-BY-4.0", [], embedding, artifact);

    private static GlobalReleaseKeyAuthorization Authorization(
        ECDsa key,
        long validFromSequence)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new GlobalReleaseKeyAuthorization(
            GlobalReleaseCodec.Sha256(publicKey),
            Convert.ToBase64String(publicKey),
            validFromSequence);
    }

    private static byte[] EncodeRelease(
        long sequence,
        string? previousReleaseSha256,
        IReadOnlyList<GlobalReleaseChange> changes,
        IReadOnlyList<GlobalResourceRecord> resultingCatalog,
        ECDsa signer)
    {
        var manifest = new GlobalReleaseManifest(
            1,
            sequence,
            sequence - 1,
            previousReleaseSha256,
            GlobalReleaseCodec.Sha256(GlobalReleaseCodec.CreateCanonicalCatalog(resultingCatalog)),
            DateTimeOffset.Parse("2026-10-17T00:00:00Z").AddDays(sequence - 1),
            changes);
        return GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(manifest, signer));
    }

    private sealed class MemoryTransport(byte[] envelope) : IGlobalRegistryTransport
    {
        private readonly GlobalReleaseDescriptor descriptor =
            new(1, GlobalReleaseCodec.Sha256(envelope), envelope.Length);

        public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<GlobalReleaseDescriptor?>(descriptor);

        public Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken) =>
            Task.FromResult(sequence == 1 ? descriptor : throw new GlobalReplicaException("Missing release."));

        public Task<byte[]> DownloadReleaseChunkAsync(long sequence, long offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(envelope.AsSpan((int)offset, Math.Min(count, envelope.Length - (int)offset)).ToArray());

        public Task<byte[]> DownloadArtifactChunkAsync(string sha256, long offset, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ChainMemoryTransport(params byte[][] envelopes) : IGlobalRegistryTransport
    {
        private readonly IReadOnlyList<byte[]> releases = envelopes;

        public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<GlobalReleaseDescriptor?>(Descriptor(releases.Count));

        public Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken) =>
            Task.FromResult(Descriptor(sequence));

        public Task<byte[]> DownloadReleaseChunkAsync(
            long sequence,
            long offset,
            int count,
            CancellationToken cancellationToken)
        {
            var envelope = Release(sequence);
            if (offset < 0 || offset >= envelope.Length)
            {
                throw new GlobalReplicaException("Missing release range.");
            }

            return Task.FromResult(
                envelope.AsSpan((int)offset, Math.Min(count, envelope.Length - (int)offset)).ToArray());
        }

        public Task<byte[]> DownloadArtifactChunkAsync(
            string sha256,
            long offset,
            int count,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private GlobalReleaseDescriptor Descriptor(long sequence)
        {
            var envelope = Release(sequence);
            return new GlobalReleaseDescriptor(sequence, GlobalReleaseCodec.Sha256(envelope), envelope.Length);
        }

        private byte[] Release(long sequence) =>
            sequence >= 1 && sequence <= releases.Count
                ? releases[(int)sequence - 1]
                : throw new GlobalReplicaException("Missing release.");
    }

    private sealed class InterruptOnceTransport(IGlobalRegistryTransport inner) : IGlobalRegistryTransport
    {
        private bool interrupted;
        private int calls;

        public List<long> Offsets { get; } = [];

        public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            inner.GetLatestReleaseAsync(cancellationToken);

        public Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken) =>
            inner.GetReleaseAsync(sequence, cancellationToken);

        public async Task<byte[]> DownloadReleaseChunkAsync(
            long sequence,
            long offset,
            int count,
            CancellationToken cancellationToken)
        {
            Offsets.Add(offset);
            calls++;
            if (!interrupted && calls == 2)
            {
                interrupted = true;
                throw new IOException("Deterministic interrupted-transfer fixture.");
            }

            return await inner.DownloadReleaseChunkAsync(sequence, offset, count, cancellationToken);
        }

        public Task<byte[]> DownloadArtifactChunkAsync(string sha256, long offset, int count, CancellationToken cancellationToken) =>
            inner.DownloadArtifactChunkAsync(sha256, offset, count, cancellationToken);
    }
}
