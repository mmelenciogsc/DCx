using System.IO;
using System.Security.Cryptography;
using DCx.Core.Reliability;

namespace DCx.Desktop;

internal sealed class ControlledUpdateFixtureSource : IControlledUpdateReviewSource
{
    private const string CurrentVersion = "1.0.0.0";
    private const string CandidateVersion = "1.1.0.0";
    private const string Identity = "DCx.AgenticBrowser";
    private const string Publisher = "CN=DCx Controlled Update Fixture";

    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-19T04:00:00Z");
    private static readonly Uri ManifestUri =
        new("https://updates.fixture.invalid/stable/manifest.json");
    private static readonly Uri PackageUri =
        new("https://updates.fixture.invalid/stable/DCx-1.1.0.0.msix");

    private readonly string stagingRoot;
    private readonly FixtureTransport transport;
    private readonly ControlledUpdateStager stager;

    public ControlledUpdateFixtureSource(string stagingRoot)
    {
        this.stagingRoot = RequireEmptyStagingRoot(stagingRoot);
        var package = "DCx inert controlled-update package fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new ControlledUpdateManifest(
            ControlledUpdateManifestCodec.CurrentSchemaVersion,
            ControlledUpdateManifestCodec.ProductId,
            CandidateVersion,
            Now,
            Now.AddDays(1),
            PackageUri.AbsoluteUri,
            package.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(package)),
            Identity,
            Publisher);
        var envelope = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(manifest, key));
        transport = new FixtureTransport(envelope, package);
        stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(),
            key.ExportSubjectPublicKeyInfo());
    }

    public async Task<ControlledUpdateSourceSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var review = await stager.ReviewAsync(
                ManifestUri,
                CurrentVersion,
                Now,
                cancellationToken)
            .ConfigureAwait(false);
        var snapshot = new ControlledUpdateSourceSnapshot(
            ControlledUpdateReviewSourcePolicy.Available,
            CurrentVersion,
            Now,
            review,
            transport.ManifestRequests,
            transport.PackageRequests,
            NetworkRequests: 0);
        ControlledUpdateReviewSourcePolicy.RequireSnapshot(snapshot);
        return snapshot;
    }

    public async Task<ControlledUpdateStageResult> StageAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        var admission = await stager.StageReviewedAsync(
                review,
                CurrentVersion,
                stagingRoot,
                Now,
                explicitlyApproved,
                cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists(admission.PackagePath) || !File.Exists(admission.SignedManifestPath))
        {
            throw new ControlledUpdateException("update_fixture_stage_missing");
        }

        var result = new ControlledUpdateStageResult(
            review.ReviewSha256,
            admission.Version,
            admission.PackageSha256,
            admission.PackageLength,
            transport.ManifestRequests,
            transport.PackageRequests,
            NetworkRequests: 0,
            ExecutionPermitted: false,
            InstallerProcessesStarted: 0,
            ElevationRequests: 0,
            ActivationAttempts: 0,
            RestartAttempts: 0,
            ProductionActions: 0);
        ControlledUpdateReviewSourcePolicy.RequireResult(review, result);
        return result;
    }

    private static string RequireEmptyStagingRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }

        var full = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volumeRoot) ||
            string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }

        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ControlledUpdateException("update_storage_invalid");
            }
        }

        return full;
    }

    private sealed class FixtureTransport(byte[] envelope, byte[] package)
        : IControlledUpdateTransport
    {
        public int ManifestRequests { get; private set; }
        public int PackageRequests { get; private set; }

        public Task<byte[]> DownloadManifestAsync(
            Uri uri,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri != ManifestUri || envelope.Length > maximumBytes)
            {
                throw new ControlledUpdateException("update_fixture_manifest_invalid");
            }

            ManifestRequests++;
            return Task.FromResult(envelope.ToArray());
        }

        public async Task<long> DownloadPackageAsync(
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (uri != PackageUri || package.LongLength > maximumBytes)
            {
                throw new ControlledUpdateException("update_fixture_package_invalid");
            }

            PackageRequests++;
            await destination.WriteAsync(package, cancellationToken).ConfigureAwait(false);
            return package.LongLength;
        }
    }

    private sealed class FixtureTrustVerifier : IUpdatePackageTrustVerifier
    {
        public Task<UpdatePackageTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(packagePath))
            {
                throw new ControlledUpdateException("update_fixture_package_missing");
            }

            return Task.FromResult(new UpdatePackageTrustEvidence(
                true,
                Identity,
                Publisher,
                CandidateVersion));
        }
    }
}
