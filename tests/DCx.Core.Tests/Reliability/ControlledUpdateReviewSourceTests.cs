using System.Reflection;
using System.Security.Cryptography;
using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class ControlledUpdateReviewSourceTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-19T04:00:00Z");
    private static readonly Uri ManifestUri =
        new("https://updates.example.test/stable/manifest.json");
    private static readonly Uri PackageUri =
        new("https://updates.example.test/stable/DCx-1.1.0.0.msix");

    [Fact]
    public async Task UnavailableSource_DoesNotInventUpdateOrRequestState()
    {
        var source = new UnavailableControlledUpdateReviewSource("1.0.0.0");

        var snapshot = await source.CheckAsync();

        ControlledUpdateReviewSourcePolicy.RequireSnapshot(snapshot);
        Assert.Equal(ControlledUpdateReviewSourcePolicy.NotConfigured, snapshot.StatusCode);
        Assert.Equal("1.0.0.0", snapshot.CurrentVersion);
        Assert.Null(snapshot.Review);
        Assert.Equal(0, snapshot.ManifestRequests);
        Assert.Equal(0, snapshot.PackageRequests);
        Assert.Equal(0, snapshot.NetworkRequests);
        await AssertCodeAsync("update_source_unavailable", () =>
            source.StageAsync(null!, explicitlyApproved: true));
    }

    [Fact]
    public async Task Policy_RequiresExactReviewAndRejectsEveryExecutionAuthority()
    {
        var review = await CreateReviewAsync();
        var snapshot = new ControlledUpdateSourceSnapshot(
            ControlledUpdateReviewSourcePolicy.Available,
            review.CurrentVersion,
            Now,
            review,
            ManifestRequests: 1,
            PackageRequests: 0,
            NetworkRequests: 0);
        ControlledUpdateReviewSourcePolicy.RequireSnapshot(snapshot);

        var result = new ControlledUpdateStageResult(
            review.ReviewSha256,
            review.CandidateVersion,
            review.PackageSha256,
            review.PackageLength,
            ManifestRequests: 2,
            PackageRequests: 1,
            NetworkRequests: 0,
            ExecutionPermitted: false,
            InstallerProcessesStarted: 0,
            ElevationRequests: 0,
            ActivationAttempts: 0,
            RestartAttempts: 0,
            ProductionActions: 0);
        ControlledUpdateReviewSourcePolicy.RequireResult(review, result);

        var invalidSnapshots = new[]
        {
            snapshot with { CurrentVersion = "1.0.0.1" },
            snapshot with { Review = null },
            snapshot with { ManifestRequests = 0 },
            snapshot with { PackageRequests = 1 },
            snapshot with { NetworkRequests = -1 },
            snapshot with { NetworkRequests = 2 },
            snapshot with { GeneratedUtc = review.ExpiresUtc },
        };
        foreach (var invalid in invalidSnapshots)
        {
            AssertCode("update_source_snapshot_invalid", () =>
                ControlledUpdateReviewSourcePolicy.RequireSnapshot(invalid));
        }

        var invalidResults = new[]
        {
            result with { ReviewSha256 = new string('0', 64) },
            result with { Version = "1.2.0.0" },
            result with { PackageSha256 = new string('0', 64) },
            result with { PackageLength = result.PackageLength + 1 },
            result with { ManifestRequests = 1 },
            result with { PackageRequests = 0 },
            result with { NetworkRequests = -1 },
            result with { NetworkRequests = 4 },
            result with { ExecutionPermitted = true },
            result with { InstallerProcessesStarted = 1 },
            result with { ElevationRequests = 1 },
            result with { ActivationAttempts = 1 },
            result with { RestartAttempts = 1 },
            result with { ProductionActions = 1 },
        };
        foreach (var invalid in invalidResults)
        {
            AssertCode("update_stage_result_invalid", () =>
                ControlledUpdateReviewSourcePolicy.RequireResult(review, invalid));
        }

        var readiness = ControlledUpdateActivationReadinessPolicy.Create(
            review,
            Now.AddMinutes(1));
        var readinessResult = new ControlledUpdateActivationReadinessResult(
            readiness,
            ManifestRequests: 2,
            PackageRequests: 1,
            NetworkRequests: 0,
            InstallerProcessesStarted: 0,
            ElevationRequests: 0,
            ActivationAttempts: 0,
            RestartAttempts: 0,
            ProductionActions: 0);
        ControlledUpdateReviewSourcePolicy.RequireActivationReadinessResult(
            review,
            readinessResult);

        var invalidReadinessResults = new[]
        {
            readinessResult with
            {
                Readiness = readiness with { ReadinessSha256 = new string('0', 64) },
            },
            readinessResult with
            {
                Readiness = readiness with { RevalidatedUtc = default },
            },
            readinessResult with
            {
                Readiness = readiness with { ReviewSha256 = new string('0', 64) },
            },
            readinessResult with
            {
                Readiness = readiness with { Version = "1.2.0.0" },
            },
            readinessResult with
            {
                Readiness = readiness with { PackageSha256 = new string('0', 64) },
            },
            readinessResult with
            {
                Readiness = readiness with { EnvelopeSha256 = new string('0', 64) },
            },
            readinessResult with
            {
                Readiness = readiness with { PackageLength = readiness.PackageLength + 1 },
            },
            readinessResult with
            {
                Readiness = readiness with { ExecutionPermitted = true },
            },
            readinessResult with { ManifestRequests = 1 },
            readinessResult with { PackageRequests = 0 },
            readinessResult with { NetworkRequests = -1 },
            readinessResult with { NetworkRequests = 4 },
            readinessResult with { InstallerProcessesStarted = 1 },
            readinessResult with { ElevationRequests = 1 },
            readinessResult with { ActivationAttempts = 1 },
            readinessResult with { RestartAttempts = 1 },
            readinessResult with { ProductionActions = 1 },
        };
        foreach (var invalid in invalidReadinessResults)
        {
            AssertCode("update_activation_readiness_result_invalid", () =>
                ControlledUpdateReviewSourcePolicy.RequireActivationReadinessResult(
                    review,
                    invalid));
        }
    }

    [Fact]
    public void UserFacingSourceContract_ExposesNoPathCommandOrSecretAuthority()
    {
        var exposedTypes = new[]
        {
            typeof(IControlledUpdateReviewSource),
            typeof(ControlledUpdateSourceSnapshot),
            typeof(ControlledUpdateStageResult),
            typeof(ControlledUpdateActivationReadinessResult),
            typeof(ControlledUpdateActivationReadiness),
            typeof(ControlledUpdateReview),
        };
        var forbidden = new[]
        {
            "path", "command", "executable", "argument", "token", "secret", "credential",
        };

        var members = exposedTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Instance | BindingFlags.Public))
            .Select(member => member.Name)
            .ToArray();
        foreach (var fragment in forbidden)
        {
            Assert.DoesNotContain(members, member =>
                member.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(
            ["CheckAsync", "PrepareActivationReadinessAsync", "StageAsync"],
            typeof(IControlledUpdateReviewSource)
                .GetMethods()
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static async Task<ControlledUpdateReview> CreateReviewAsync()
    {
        var package = "controlled update source fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new ControlledUpdateManifest(
            ControlledUpdateManifestCodec.CurrentSchemaVersion,
            ControlledUpdateManifestCodec.ProductId,
            "1.1.0.0",
            Now,
            Now.AddDays(1),
            PackageUri.AbsoluteUri,
            package.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(package)),
            "DCx.AgenticBrowser",
            "CN=DCx Test Publisher");
        var envelope = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(manifest, key));
        var stager = new ControlledUpdateStager(
            new ManifestOnlyTransport(envelope),
            new NeverCalledTrustVerifier(),
            key.ExportSubjectPublicKeyInfo());
        return await stager.ReviewAsync(ManifestUri, "1.0.0.0", Now);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
    }

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
    }

    private sealed class ManifestOnlyTransport(byte[] envelope) : IControlledUpdateTransport
    {
        public Task<byte[]> DownloadManifestAsync(
            Uri uri,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ManifestUri, uri);
            Assert.True(envelope.Length <= maximumBytes);
            return Task.FromResult(envelope.ToArray());
        }

        public Task<long> DownloadPackageAsync(
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Review must not download a package.");
    }

    private sealed class NeverCalledTrustVerifier : IUpdatePackageTrustVerifier
    {
        public Task<UpdatePackageTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Review must not invoke package trust.");
    }
}
