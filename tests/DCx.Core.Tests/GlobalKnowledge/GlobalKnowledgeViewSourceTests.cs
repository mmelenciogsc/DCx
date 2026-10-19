using System.Security.Cryptography;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class GlobalKnowledgeViewSourceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-p09-view-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReplicaSource_ReportsSignedStateSearchesAndRequiresTransportForSync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var resource = new GlobalResourceRecord(
            "global:accessible-browser",
            "1.0.0",
            KnowledgeResourceKind.Ckb,
            "Accessible browser patterns",
            "Keyboard focus restoration and screen reader announcements.",
            "https://example.test/accessible-browser",
            "CC-BY-4.0",
            ["keyboard", "screen-reader"]);
        var changes = new[]
        {
            new GlobalReleaseChange(
                GlobalReleaseChangeKind.Upsert,
                resource.ResourceId,
                resource.Version,
                resource),
        };
        var catalog = GlobalCatalogReducer.ApplyChanges([], changes);
        var manifest = new GlobalReleaseManifest(
            GlobalReleaseCodec.CurrentSchemaVersion,
            1,
            0,
            null,
            GlobalReleaseCodec.Sha256(GlobalReleaseCodec.CreateCanonicalCatalog(catalog)),
            DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
            changes);
        var envelope = GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(manifest, key));
        var transport = new SingleReleaseTransport(envelope);
        var client = await GlobalReplicaClient.OpenAsync(
            root,
            key.ExportSubjectPublicKeyInfo(),
            new TestGlobalTrustPolicyAnchorStore());
        var source = new GlobalReplicaViewSource(client, transport);

        var empty = await source.GetStatusAsync();
        Assert.True(empty.IsConfigured);
        Assert.True(empty.CanSynchronize);
        Assert.Equal("replica_empty", empty.StatusCode);
        Assert.Equal(0, empty.Replica!.Sequence);

        var sync = await source.SynchronizeAsync();
        Assert.Equal(1, sync.ReleasesApplied);
        var ready = await source.GetStatusAsync();
        Assert.Equal("replica_ready", ready.StatusCode);
        Assert.Equal(1, ready.Replica!.Sequence);
        Assert.Equal("global:accessible-browser", Assert.Single(
            await source.SearchAsync("keyboard screen reader")).ResourceId);

        var offline = new GlobalReplicaViewSource(client);
        Assert.False((await offline.GetStatusAsync()).CanSynchronize);
        await Assert.ThrowsAsync<GlobalReplicaException>(() => offline.SynchronizeAsync());
        Assert.Equal("global:accessible-browser", Assert.Single(
            await offline.SearchAsync("focus restoration")).ResourceId);
    }

    [Fact]
    public async Task UnavailableSource_ReturnsExplicitNoInferenceState()
    {
        var source = new UnavailableGlobalKnowledgeViewSource();

        var status = await source.GetStatusAsync();

        Assert.False(status.IsConfigured);
        Assert.False(status.CanSynchronize);
        Assert.Equal("not_configured", status.StatusCode);
        Assert.Null(status.Replica);
        Assert.Null(status.TrustPolicy);
        Assert.Empty(await source.SearchAsync("anything"));
        await Assert.ThrowsAsync<GlobalReplicaException>(() => source.SynchronizeAsync());
    }

    [Fact]
    public async Task ReplicaSearch_RejectsOversizedOrControlBearingQueriesBeforeStorageWork()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = await GlobalReplicaClient.OpenAsync(
            Path.Combine(root, "query-bounds"),
            key.ExportSubjectPublicKeyInfo(),
            new TestGlobalTrustPolicyAnchorStore());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SearchAsync(new string('a', GlobalReplicaClient.MaximumSearchQueryCharacters + 1), null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SearchAsync("safe\u0000unsafe", null));
        Assert.Empty(await client.SearchAsync("line one\nline two", null));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private sealed class SingleReleaseTransport(byte[] envelope) : IGlobalRegistryTransport
    {
        private readonly GlobalReleaseDescriptor descriptor =
            new(1, GlobalReleaseCodec.Sha256(envelope), envelope.Length);

        public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<GlobalReleaseDescriptor?>(descriptor);

        public Task<GlobalReleaseDescriptor> GetReleaseAsync(
            long sequence,
            CancellationToken cancellationToken) =>
            Task.FromResult(sequence == 1
                ? descriptor
                : throw new GlobalReplicaException("The fixture release is unavailable."));

        public Task<byte[]> DownloadReleaseChunkAsync(
            long sequence,
            long offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sequence != 1 || offset < 0 || offset >= envelope.Length)
            {
                throw new GlobalReplicaException("The fixture release range is unavailable.");
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
    }
}
