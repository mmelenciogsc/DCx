using System.Security.Cryptography;
using System.Text;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;
using Microsoft.Data.Sqlite;

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
        var clientA = await GlobalReplicaClient.OpenAsync(
            clientARoot, repository.TrustRootSubjectPublicKeyInfo, TestGlobalTrustPolicyAnchorStore.Shared);
        var clientB = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "client-b"), repository.TrustRootSubjectPublicKeyInfo,
            TestGlobalTrustPolicyAnchorStore.Shared);

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
    public async Task SameReplicaClients_SerializeSyncAndCancellationWithoutCheckpointRace()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var repository = await GlobalRegistryRepository.OpenAsync(
            Path.Combine(root, "shared-replica-server"),
            key.ExportPkcs8PrivateKey());
        var resource = Resource(
            "global:shared-replica",
            "1",
            "Shared replica",
            "Concurrent clients must observe one atomic activation.",
            [1f]);
        await repository.PublishAsync(
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
            DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        var transport = new RepositoryRegistryTransport(repository);
        var blockingTransport = new BlockingReleaseTransport(transport);
        var clientRoot = Path.Combine(root, "shared-replica-client");
        var anchors = new TestGlobalTrustPolicyAnchorStore();
        var firstClient = await GlobalReplicaClient.OpenAsync(
            clientRoot,
            repository.TrustRootSubjectPublicKeyInfo,
            anchors);
        var secondClient = await GlobalReplicaClient.OpenAsync(
            clientRoot,
            repository.TrustRootSubjectPublicKeyInfo,
            anchors);
        var cancellingClient = await GlobalReplicaClient.OpenAsync(
            clientRoot,
            repository.TrustRootSubjectPublicKeyInfo,
            anchors);

        var firstSync = firstClient.SynchronizeAsync(blockingTransport);
        await blockingTransport.WaitUntilBlockedAsync();
        var secondSync = secondClient.SynchronizeAsync(transport);
        try
        {
            await Task.Delay(100);
            Assert.False(secondSync.IsCompleted);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cancellingClient.GetStatusAsync(cancellation.Token));
        }
        finally
        {
            blockingTransport.Release();
        }

        var firstResult = await firstSync;
        var secondResult = await secondSync;
        Assert.Equal(1, firstResult.ReleasesApplied);
        Assert.Equal(0, secondResult.ReleasesApplied);
        Assert.True(secondResult.WasAlreadyCurrent);
        Assert.Equal(1, (await cancellingClient.GetStatusAsync()).Sequence);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(firstClient.ReplicaRoot, "staging"),
            "*.part",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task OperationLease_CancelsOnExternalContentionThenRecovers()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "externally-contended-client"),
            key.ExportSubjectPublicKeyInfo(),
            new TestGlobalTrustPolicyAnchorStore());
        var lockPath = Path.Combine(client.ReplicaRoot, GlobalReplicaOperationLease.LockFileName);

        using (var externalLease = new FileStream(
                   lockPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetStatusAsync(cancellation.Token));
        }

        Assert.Equal(0, (await client.GetStatusAsync()).Sequence);
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
            trusted.ExportSubjectPublicKeyInfo(),
            TestGlobalTrustPolicyAnchorStore.Shared);

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
            trustRoot.ExportSubjectPublicKeyInfo(),
            TestGlobalTrustPolicyAnchorStore.Shared);

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
            trustRoot.ExportSubjectPublicKeyInfo(),
            TestGlobalTrustPolicyAnchorStore.Shared);
        Assert.Equal(3, (await reopened.GetTrustPolicyStatusAsync())!.Revision);
        Assert.Equal(2, (await reopened.GetStatusAsync()).Sequence);
    }

    [Fact]
    public async Task ProtectedTrustAnchor_RejectsFilesystemRollbackAndRecoversInterruptedAdvance()
    {
        using var trustRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustBytes = trustRoot.ExportSubjectPublicKeyInfo();
        var trustId = GlobalReleaseCodec.Sha256(trustBytes);
        var authorization = Authorization(releaseKey, 1);
        var policyOne = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1, 1, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"), [authorization]),
            trustRoot));
        var policyTwo = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1,
                2,
                GlobalReleaseCodec.Sha256(policyOne),
                DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
                [authorization]),
            trustRoot));
        var policyThree = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1,
                3,
                GlobalReleaseCodec.Sha256(policyTwo),
                DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
                [authorization]),
            trustRoot));
        var anchors = new TestGlobalTrustPolicyAnchorStore();
        var clientRoot = Path.Combine(root, "protected-anchor-client");
        var client = await GlobalReplicaClient.OpenAsync(clientRoot, trustBytes, anchors);

        await client.ApplyTrustPolicyAsync(policyOne);
        await client.ApplyTrustPolicyAsync(policyTwo);
        Assert.Equal(2, anchors.Get(trustId)!.Revision);
        var policyPath = Path.Combine(client.ReplicaRoot, "trust-policy.json");

        await File.WriteAllBytesAsync(policyPath, policyOne);
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalReplicaClient.OpenAsync(clientRoot, trustBytes, anchors));
        Assert.Equal(2, anchors.Get(trustId)!.Revision);

        await File.WriteAllBytesAsync(policyPath, policyTwo);
        var restored = await GlobalReplicaClient.OpenAsync(clientRoot, trustBytes, anchors);
        anchors.FailNextAdvance = true;
        await Assert.ThrowsAsync<GlobalReplicaException>(() => restored.ApplyTrustPolicyAsync(policyThree));
        Assert.Equal(2, anchors.Get(trustId)!.Revision);

        var recovered = await GlobalReplicaClient.OpenAsync(clientRoot, trustBytes, anchors);
        Assert.Equal(3, (await recovered.GetTrustPolicyStatusAsync())!.Revision);
        Assert.Equal(3, anchors.Get(trustId)!.Revision);

        File.Delete(policyPath);
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalReplicaClient.OpenAsync(clientRoot, trustBytes, anchors));
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
            repository.TrustRootSubjectPublicKeyInfo,
            TestGlobalTrustPolicyAnchorStore.Shared);

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

    [Fact]
    public async Task Registry_IdempotentPublicationSurvivesReopenAndRejectsChangedRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        var serverRoot = Path.Combine(root, "idempotent-server");
        var firstResource = Resource("global:idempotent-a", "1", "First", "First idempotent resource.", [1f]);
        var secondResource = Resource("global:idempotent-b", "1", "Second", "Second idempotent resource.", [0f, 1f]);
        var firstChange = new GlobalReleaseChange(
            GlobalReleaseChangeKind.Upsert, firstResource.ResourceId, firstResource.Version, firstResource);
        var secondChange = new GlobalReleaseChange(
            GlobalReleaseChangeKind.Upsert, secondResource.ResourceId, secondResource.Version, secondResource);
        GlobalReleaseDescriptor initial;
        await using (var repository = await GlobalRegistryRepository.OpenAsync(serverRoot, privateKey))
        {
            initial = await repository.PublishIdempotentAsync(
                "fixture:idempotent-publication", [secondChange, firstChange], DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        }

        await using var reopened = await GlobalRegistryRepository.OpenAsync(serverRoot, privateKey);
        var replay = await reopened.PublishIdempotentAsync(
            "fixture:idempotent-publication", [firstChange, secondChange], DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        var changed = firstResource with { Summary = "A conflicting changed request." };
        var conflict = await Assert.ThrowsAsync<GlobalReplicaException>(() => reopened.PublishIdempotentAsync(
            "fixture:idempotent-publication",
            [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, changed.ResourceId, changed.Version, changed)],
            DateTimeOffset.Parse("2026-10-19T00:00:00Z")));

        Assert.Equal(initial, replay);
        Assert.Equal(1, (await reopened.GetLatestAsync())!.Sequence);
        Assert.Contains("reused", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StorageBoundary_RejectsReparseAncestorAndSwappedClientDirectory()
    {
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside-client-storage");
        Directory.CreateDirectory(outside);
        var linkedApplicationRoot = Path.Combine(root, "linked-client-root");
        Directory.CreateSymbolicLink(linkedApplicationRoot, outside);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();

        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalReplicaClient.OpenAsync(
                Path.GetPathRoot(root)!,
                publicKey,
                TestGlobalTrustPolicyAnchorStore.Shared));
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalReplicaClient.OpenAsync(
                linkedApplicationRoot,
                publicKey,
                TestGlobalTrustPolicyAnchorStore.Shared));

        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "safe-client"),
            publicKey,
            TestGlobalTrustPolicyAnchorStore.Shared);
        var staging = Path.Combine(client.ReplicaRoot, "staging");
        Directory.Delete(staging);
        var outsideStaging = Path.Combine(root, "outside-staging");
        Directory.CreateDirectory(outsideStaging);
        Directory.CreateSymbolicLink(staging, outsideStaging);

        await Assert.ThrowsAsync<GlobalReplicaException>(() => client.GetStatusAsync());
        Assert.Empty(Directory.GetFileSystemEntries(outsideStaging));

        var pointerClient = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "pointer-client"),
            publicKey,
            TestGlobalTrustPolicyAnchorStore.Shared);
        var outsidePointer = Path.Combine(root, "outside-active.json");
        await File.WriteAllTextAsync(outsidePointer, "{}");
        File.CreateSymbolicLink(
            Path.Combine(pointerClient.ReplicaRoot, "active.json"),
            outsidePointer);
        await Assert.ThrowsAsync<GlobalReplicaException>(() => pointerClient.GetStatusAsync());

        var lockClient = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "lock-client"),
            publicKey,
            new TestGlobalTrustPolicyAnchorStore());
        var lockPath = Path.Combine(lockClient.ReplicaRoot, GlobalReplicaOperationLease.LockFileName);
        File.Delete(lockPath);
        var outsideLock = Path.Combine(root, "outside-lock");
        await File.WriteAllTextAsync(outsideLock, "must remain unchanged");
        File.CreateSymbolicLink(lockPath, outsideLock);

        await Assert.ThrowsAsync<GlobalReplicaException>(() => lockClient.GetStatusAsync());
        Assert.Equal("must remain unchanged", await File.ReadAllTextAsync(outsideLock));
    }

    [Fact]
    public async Task AuthoritativeStorage_RejectsReparseRootsSwapsAndNonP256Key()
    {
        Directory.CreateDirectory(root);
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            GlobalRegistryRepository.OpenAsync(
                Path.Combine(root, "wrong-key-server"),
                p384.ExportPkcs8PrivateKey()));

        var outside = Path.Combine(root, "outside-server-storage");
        Directory.CreateDirectory(outside);
        var linkedRoot = Path.Combine(root, "linked-server-root");
        Directory.CreateSymbolicLink(linkedRoot, outside);
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalRegistryRepository.OpenAsync(linkedRoot, p256.ExportPkcs8PrivateKey()));

        var linkedDatabaseRoot = Path.Combine(root, "linked-database-server");
        Directory.CreateDirectory(linkedDatabaseRoot);
        var outsideDatabase = Path.Combine(root, "outside-registry.sqlite3");
        await File.WriteAllBytesAsync(outsideDatabase, [0]);
        File.CreateSymbolicLink(
            Path.Combine(linkedDatabaseRoot, "authoritative-registry.sqlite3"),
            outsideDatabase);
        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            GlobalRegistryRepository.OpenAsync(
                linkedDatabaseRoot,
                p256.ExportPkcs8PrivateKey()));

        var metadataRoot = Path.Combine(root, "metadata-server");
        await using (var metadataRepository = await GlobalRegistryRepository.OpenAsync(
                         metadataRoot,
                         p256.ExportPkcs8PrivateKey()))
        {
            var artifact = await metadataRepository.AddArtifactAsync(
                "digest-bound path"u8.ToArray(),
                "application/octet-stream");
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(metadataRoot, "authoritative-registry.sqlite3"),
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifacts SET relative_path = '../outside' WHERE sha256 = $sha;";
            command.Parameters.AddWithValue("$sha", artifact.Sha256);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            await Assert.ThrowsAsync<GlobalReplicaException>(() =>
                metadataRepository.ReadArtifactChunkAsync(artifact.Sha256, 0, 1));
        }

        var serverRoot = Path.Combine(root, "safe-server");
        await using var repository = await GlobalRegistryRepository.OpenAsync(
            serverRoot,
            p256.ExportPkcs8PrivateKey());
        var artifactStore = Path.Combine(serverRoot, "artifacts", "sha256");
        Directory.Delete(artifactStore);
        var outsideArtifacts = Path.Combine(root, "outside-artifacts");
        Directory.CreateDirectory(outsideArtifacts);
        Directory.CreateSymbolicLink(artifactStore, outsideArtifacts);

        await Assert.ThrowsAsync<GlobalReplicaException>(() =>
            repository.AddArtifactAsync("must not escape"u8.ToArray(), "application/octet-stream"));
        Assert.Empty(Directory.GetFileSystemEntries(outsideArtifacts));
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
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

    private sealed class BlockingReleaseTransport(IGlobalRegistryTransport inner) : IGlobalRegistryTransport
    {
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int shouldBlock = 1;

        public Task WaitUntilBlockedAsync() => blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => released.TrySetResult();

        public Task<GlobalTrustPolicyDescriptor?> GetLatestTrustPolicyAsync(CancellationToken cancellationToken) =>
            inner.GetLatestTrustPolicyAsync(cancellationToken);

        public Task<GlobalTrustPolicyDescriptor> GetTrustPolicyAsync(
            long revision,
            CancellationToken cancellationToken) =>
            inner.GetTrustPolicyAsync(revision, cancellationToken);

        public Task<byte[]> DownloadTrustPolicyChunkAsync(
            long revision,
            long offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.DownloadTrustPolicyChunkAsync(revision, offset, count, cancellationToken);

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
            if (Interlocked.Exchange(ref shouldBlock, 0) == 1)
            {
                blocked.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }

            return await inner.DownloadReleaseChunkAsync(sequence, offset, count, cancellationToken);
        }

        public Task<byte[]> DownloadArtifactChunkAsync(
            string sha256,
            long offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.DownloadArtifactChunkAsync(sha256, offset, count, cancellationToken);
    }
}
