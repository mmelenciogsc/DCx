using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Knowledge;
using DCx.Core.Orchestration;

namespace DCx.Core.Tests.Knowledge;

public sealed class PrivateKnowledgeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-p08-{Guid.NewGuid():N}");

    [Fact]
    public async Task Store_InsertsHybridSearchesVersionsAndReopensOffline()
    {
        var store = await PrivateKnowledgeStore.OpenAsync(root, "profile-a");
        await store.PutAsync(Ckb(
            "docs:browser",
            "1.0.0",
            "Accessible browser shortcuts",
            "Keyboard navigation and screen reader focus for WebView2.",
            [1f, 0f]), null);
        await store.PutAsync(Ckb(
            "docs:finance",
            "1.0.0",
            "Financial reconciliation",
            "A sandbox ledger and payout reconciliation fixture.",
            [0f, 1f]), null);

        var results = await store.SearchAsync("keyboard browser", [0.95f, 0.05f]);

        Assert.Equal("docs:browser", results[0].Resource.ResourceId);
        var update = Ckb(
            "docs:browser",
            "2.0.0",
            "Accessible browser keyboard guide",
            "Updated keyboard navigation, focus restoration, and screen reader guidance.",
            [1f, 0f]) with
        {
            Dependencies = [new KnowledgeDependency("docs:platform", ">=1.0.0")],
        };
        await store.PutAsync(update, "1.0.0");
        await Assert.ThrowsAsync<KnowledgeVersionConflictException>(() =>
            store.PutAsync(update with { Version = "3.0.0" }, "1.0.0"));

        var reopened = await PrivateKnowledgeStore.OpenAsync(root, "profile-a");
        var resource = await reopened.GetAsync("docs:browser");
        var offlineResults = await reopened.SearchAsync("focus restoration", null);

        Assert.Equal("2.0.0", resource!.Version);
        Assert.Equal("docs:platform", Assert.Single(resource.Dependencies).ResourceId);
        Assert.Equal("docs:browser", Assert.Single(offlineResults).Resource.ResourceId);
    }

    [Fact]
    public async Task Store_PhysicallyIsolatesProfiles()
    {
        var alice = await PrivateKnowledgeStore.OpenAsync(root, "alice");
        var bob = await PrivateKnowledgeStore.OpenAsync(root, "bob");
        await alice.PutAsync(Ckb("private:item", "1", "Alice private note", "Only Alice owns this body.", [1f]), null);

        Assert.NotEqual(alice.StoreRoot, bob.StoreRoot);
        Assert.NotNull(await alice.GetAsync("private:item"));
        Assert.Null(await bob.GetAsync("private:item"));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(root, "private")).Length);
    }

    [Fact]
    public async Task ArtifactStore_RejectsDigestMismatchAndTraversalPackage()
    {
        var store = await PrivateKnowledgeStore.OpenAsync(root, "profile-a");
        var valid = CreatePackage("tool:fixture", "1.0.0", "tool/run.txt");
        var digest = Sha256(valid);

        await Assert.ThrowsAsync<KnowledgeArtifactException>(() =>
            store.ArtifactStore.PutAsync(valid, new string('0', 64), new CcbPackageValidator()));

        var malicious = CreatePackage("tool:fixture", "1.0.0", "../run.txt");
        await Assert.ThrowsAsync<KnowledgeArtifactException>(() =>
            store.ArtifactStore.PutAsync(malicious, Sha256(malicious), new CcbPackageValidator()));

        Assert.Equal(digest, await store.ArtifactStore.PutAsync(valid, digest, new CcbPackageValidator()));
        Assert.True(File.Exists(store.ArtifactStore.ResolveExisting(digest)));
    }

    [Fact]
    public async Task SignedCcb_IsVerifiedTestedAndReusedByBoss()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = new TrustedCcbPublisher("fixture-publisher", signer.ExportSubjectPublicKeyInfo());
        var store = await PrivateKnowledgeStore.OpenAsync(root, "profile-a", [publisher]);
        var package = CreatePackage("tool:upper", "1.0.0", "tool/run.txt");
        var digest = Sha256(package);
        await store.ArtifactStore.PutAsync(package, digest, new CcbPackageValidator());

        var unsigned = new KnowledgeResourceDraft(
            "tool:upper",
            "1.0.0",
            KnowledgeResourceKind.Ccb,
            "Uppercase fixture",
            "A deterministic registered adapter used only by the P08 harness.",
            "urn:dcx:test:upper",
            "MIT",
            ["text.upper"],
            [],
            ArtifactSha256: digest,
            Provenance: new CcbProvenanceEnvelope("fixture-publisher", string.Empty));
        var signature = signer.SignData(CcbProvenanceVerifier.CreatePayload(unsigned), HashAlgorithmName.SHA256);
        var signed = unsigned with
        {
            Provenance = new CcbProvenanceEnvelope("fixture-publisher", Convert.ToBase64String(signature)),
        };
        await store.PutAsync(signed, null);

        var resource = await store.GetAsync("tool:upper");
        var tool = new UppercaseTool(digest);
        var expected = Sha256(Encoding.UTF8.GetBytes("HELLO"));
        var harness = await new CcbTestHarness().RunAsync(
            resource!,
            tool,
            [new CcbToolTestCase("uppercase", Encoding.UTF8.GetBytes("hello"), expected)],
            TimeSpan.FromSeconds(2));
        var capability = new VettedCcbCapability(resource!, tool, harness);
        var task = new BossTaskDefinition(
            "fixture",
            "Reuse tested CCB",
            BossSpecialistRole.Executor,
            "text.upper",
            [],
            new HashSet<string>(),
            new BossTaskBudget(1, 100, TimeSpan.FromSeconds(2)));

        var selected = new BossRouter().Select(task, [capability]);
        var selectedCapability = selected.Capability ?? throw new InvalidOperationException("Expected a vetted CCB capability.");
        var result = await selectedCapability.ExecuteAsync(
            new BossTaskContext("fixture", "Reuse tested CCB", BossSpecialistRole.Executor, 1,
                new Dictionary<string, BossTaskOutcome>()),
            CancellationToken.None);

        Assert.True(harness.Passed);
        Assert.Equal(BossRouteKind.VettedCcb, selectedCapability.RouteKind);
        Assert.True(result.Succeeded);
        Assert.Contains("ccb-tested:tool:upper:1.0.0", result.Evidence);

        var tampered = signed with { Body = "Changed after the publisher signature." };
        Assert.Throws<KnowledgeArtifactException>(() =>
            new CcbProvenanceVerifier([publisher]).Verify(tampered));
    }

    [Fact]
    public async Task SignedCcb_DetectsStoredArtifactTamperingOnRead()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = new TrustedCcbPublisher("fixture-publisher", signer.ExportSubjectPublicKeyInfo());
        var store = await PrivateKnowledgeStore.OpenAsync(root, "profile-a", [publisher]);
        var package = CreatePackage("tool:upper", "1.0.0", "tool/run.txt");
        var digest = await store.ArtifactStore.PutAsync(package, Sha256(package), new CcbPackageValidator());
        var draft = Sign(new KnowledgeResourceDraft(
            "tool:upper", "1.0.0", KnowledgeResourceKind.Ccb, "Uppercase fixture", "Fixture body.",
            "urn:dcx:test:upper", "MIT", ["text.upper"], [], ArtifactSha256: digest), signer);
        await store.PutAsync(draft, null);
        await File.WriteAllTextAsync(store.ArtifactStore.ResolveExisting(digest), "tampered");

        await Assert.ThrowsAsync<KnowledgeArtifactException>(() => store.GetAsync("tool:upper"));
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    private static KnowledgeResourceDraft Ckb(
        string id,
        string version,
        string title,
        string body,
        float[] embedding) =>
        new(id, version, KnowledgeResourceKind.Ckb, title, body, "urn:dcx:test", "CC-BY-4.0", [], [], embedding);

    private static byte[] CreatePackage(string id, string version, string entrypoint)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("dcx-tool.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
            {
                writer.Write($"{{\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"{version}\",\"entrypoint\":\"{entrypoint}\"}}");
            }

            var tool = archive.CreateEntry(entrypoint, CompressionLevel.Optimal);
            using var toolWriter = new StreamWriter(tool.Open(), new UTF8Encoding(false));
            toolWriter.Write("fixture");
        }

        return stream.ToArray();
    }

    private static KnowledgeResourceDraft Sign(KnowledgeResourceDraft draft, ECDsa signer)
    {
        var unsigned = draft with { Provenance = new CcbProvenanceEnvelope("fixture-publisher", string.Empty) };
        var signature = signer.SignData(CcbProvenanceVerifier.CreatePayload(unsigned), HashAlgorithmName.SHA256);
        return unsigned with
        {
            Provenance = unsigned.Provenance with { SignatureBase64 = Convert.ToBase64String(signature) },
        };
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed class UppercaseTool(string artifactSha256) : IRegisteredCcbTool
    {
        public string Id => "tool:upper";

        public string Version => "1.0.0";

        public string ArtifactSha256 => artifactSha256;

        public Task<byte[]> ExecuteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input.Span).ToUpperInvariant()));
    }
}
