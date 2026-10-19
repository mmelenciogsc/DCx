using System.Security.Cryptography;
using System.Net;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Security;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class GlobalRegistryEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-global-enrollment-{Guid.NewGuid():N}");
    private readonly DateTimeOffset reviewedUtc =
        DateTimeOffset.Parse("2026-10-19T01:00:00Z");

    [Fact]
    public async Task Reviewer_BindsExactP256RootAndCanonicalEndpoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = await WriteKeyAsync("trust-root.spki", key.ExportSubjectPublicKeyInfo());

        using var review = await new GlobalRegistryEnrollmentReviewer().ReviewAsync(
            path,
            "https://registry.example.test/dcx",
            reviewedUtc);

        Assert.Equal("trust-root.spki", review.FileName);
        Assert.Equal(new FileInfo(path).Length, review.TrustRootBytes);
        Assert.Equal("https://registry.example.test/dcx/", review.RegistryBaseUri.AbsoluteUri);
        Assert.Equal(GlobalReleaseCodec.Sha256(key.ExportSubjectPublicKeyInfo()), review.TrustRootSha256);
        Assert.Equal(reviewedUtc, review.ReviewedUtc);
    }

    [Fact]
    public async Task Reviewer_RejectsRemoteHttpNonP256AndLinkedCandidate()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var p384Path = await WriteKeyAsync("p384.spki", p384.ExportSubjectPublicKeyInfo());
        var reviewer = new GlobalRegistryEnrollmentReviewer();

        await Assert.ThrowsAsync<GlobalReplicaException>(() => reviewer.ReviewAsync(
            p384Path,
            "https://registry.example.test/",
            reviewedUtc));
        await Assert.ThrowsAsync<GlobalReplicaException>(() => reviewer.ReviewAsync(
            p384Path,
            "http://registry.example.test/",
            reviewedUtc));

        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = await WriteKeyAsync("real.spki", p256.ExportSubjectPublicKeyInfo());
        var linked = Path.Combine(root, "linked.spki");
        File.CreateSymbolicLink(linked, real);
        await Assert.ThrowsAsync<GlobalReplicaException>(() => reviewer.ReviewAsync(
            linked,
            "http://127.0.0.1:5087/",
            reviewedUtc));
    }

    [Fact]
    public async Task WindowsVaultStore_RequiresApprovalRoundTripsReenrollsAndClears()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = "DCx/Test/GlobalRegistryEnrollment/" + Guid.NewGuid().ToString("N");
        var vault = new WindowsCredentialVault();
        var store = new WindowsGlobalRegistryEnrollmentStore(vault, target);
        try
        {
            using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var firstPath = await WriteKeyAsync("first.spki", firstKey.ExportSubjectPublicKeyInfo());
            using var first = await new GlobalRegistryEnrollmentReviewer().ReviewAsync(
                firstPath,
                "https://registry.example.test/dcx/",
                reviewedUtc);

            await Assert.ThrowsAsync<GlobalReplicaException>(() => store.SaveAsync(
                first,
                explicitlyApproved: false,
                reviewedUtc.AddMinutes(1)));
            await Assert.ThrowsAsync<GlobalReplicaException>(() => store.SaveAsync(
                first,
                explicitlyApproved: true,
                reviewedUtc.AddMinutes(31)));
            Assert.Null(await store.ReadAsync());

            await store.SaveAsync(first, true, reviewedUtc.AddMinutes(1));
            var enrolled = Assert.IsType<GlobalRegistryEnrollment>(await store.ReadAsync());
            Assert.Equal("https://registry.example.test/dcx/", enrolled.RegistryBaseUri);
            Assert.Equal(first.TrustRootSha256, enrolled.TrustRootSha256);
            Assert.Equal(firstKey.ExportSubjectPublicKeyInfo(),
                GlobalRegistryEnrollmentPolicy.ExportTrustRoot(enrolled));

            using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var secondPath = await WriteKeyAsync("second.spki", secondKey.ExportSubjectPublicKeyInfo());
            using var second = await new GlobalRegistryEnrollmentReviewer().ReviewAsync(
                secondPath,
                "http://[::1]:5087/",
                reviewedUtc.AddMinutes(2));
            await store.SaveAsync(second, true, reviewedUtc.AddMinutes(3));
            var replaced = Assert.IsType<GlobalRegistryEnrollment>(await store.ReadAsync());
            Assert.Equal(second.TrustRootSha256, replaced.TrustRootSha256);
            Assert.Equal("http://[::1]:5087/", replaced.RegistryBaseUri);

            await store.ClearAsync();
            Assert.Null(await store.ReadAsync());
            await store.ClearAsync();
        }
        finally
        {
            _ = vault.Delete(target);
        }
    }

    [Fact]
    public void Policy_RejectsDigestConflictAndNonCanonicalEndpoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = key.ExportSubjectPublicKeyInfo();
        var enrollment = new GlobalRegistryEnrollment(
            WindowsGlobalRegistryEnrollmentStore.CurrentSchemaVersion,
            "https://registry.example.test/dcx/",
            Convert.ToBase64String(bytes),
            GlobalReleaseCodec.Sha256(bytes),
            reviewedUtc);

        GlobalRegistryEnrollmentPolicy.Validate(enrollment);
        Assert.Throws<GlobalReplicaException>(() => GlobalRegistryEnrollmentPolicy.Validate(
            enrollment with { TrustRootSha256 = new string('a', 64) }));
        Assert.Throws<GlobalReplicaException>(() => GlobalRegistryEnrollmentPolicy.Validate(
            enrollment with { RegistryBaseUri = "https://registry.example.test/dcx" }));
        Assert.Throws<GlobalReplicaException>(() => GlobalRegistryEnrollmentPolicy.Validate(
            enrollment with { TrustRootSubjectPublicKeyInfoBase64 = "not-base64" }));
        Assert.Throws<GlobalReplicaException>(() => GlobalRegistryEnrollmentPolicy.Validate(
            enrollment with { TrustRootSubjectPublicKeyInfoBase64 = null! }));
    }

    [Fact]
    public async Task SourceFactory_WithoutEnrollmentDoesNotOpenStorageOrCreateNetworkHandler()
    {
        var applicationData = Path.Combine(root, "unenrolled-data");
        var handlerCreated = false;

        var configuration = await GlobalKnowledgeSourceFactory.CreateCoreAsync(
            applicationData,
            new FixedEnrollmentStore(null),
            new TestGlobalTrustPolicyAnchorStore(),
            () =>
            {
                handlerCreated = true;
                return new CountingHandler();
            });

        var status = await configuration.Source.GetStatusAsync();
        Assert.False(configuration.IsEnrolled);
        Assert.False(status.IsConfigured);
        Assert.False(handlerCreated);
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public async Task SourceFactory_OpensProtectedEnrollmentOfflineAndContactsOnlyOnExplicitSync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = key.ExportSubjectPublicKeyInfo();
        var enrollment = new GlobalRegistryEnrollment(
            WindowsGlobalRegistryEnrollmentStore.CurrentSchemaVersion,
            "https://registry.example.test/dcx/",
            Convert.ToBase64String(bytes),
            GlobalReleaseCodec.Sha256(bytes),
            reviewedUtc);
        var handler = new CountingHandler();

        var configuration = await GlobalKnowledgeSourceFactory.CreateCoreAsync(
            Path.Combine(root, "enrolled-data"),
            new FixedEnrollmentStore(enrollment),
            new TestGlobalTrustPolicyAnchorStore(),
            () => handler);
        try
        {
            var status = await configuration.Source.GetStatusAsync();
            var results = await configuration.Source.SearchAsync("offline", 20);

            Assert.True(configuration.IsEnrolled);
            Assert.True(status.IsConfigured);
            Assert.True(status.CanSynchronize);
            Assert.Empty(results);
            Assert.Equal(0, handler.RequestCount);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => configuration.Source.SynchronizeAsync());
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(
                "https://registry.example.test/dcx/api/trust-policy/latest",
                handler.LastRequestUri?.AbsoluteUri);
        }
        finally
        {
            (configuration.Source as IDisposable)?.Dispose();
        }

        Assert.True(handler.WasDisposed);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<string> WriteKeyAsync(string name, byte[] bytes)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private sealed class FixedEnrollmentStore(GlobalRegistryEnrollment? enrollment)
        : IGlobalRegistryEnrollmentStore
    {
        public Task<GlobalRegistryEnrollment?> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(enrollment);
        }

        public Task SaveAsync(
            GlobalRegistryEnrollmentReview review,
            bool explicitlyApproved,
            DateTimeOffset approvedUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public bool WasDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
            });
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
