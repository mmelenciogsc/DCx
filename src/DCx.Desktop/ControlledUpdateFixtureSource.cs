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
    private int activationValidationConsumed;

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

    public async Task<ControlledUpdateActivationReadinessResult> PrepareActivationReadinessAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        var readiness = await stager.PrepareActivationReadinessAsync(
                review,
                CurrentVersion,
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved,
                cancellationToken)
            .ConfigureAwait(false);
        var result = new ControlledUpdateActivationReadinessResult(
            readiness,
            transport.ManifestRequests,
            transport.PackageRequests,
            NetworkRequests: 0,
            InstallerProcessesStarted: 0,
            ElevationRequests: 0,
            ActivationAttempts: 0,
            RestartAttempts: 0,
            ProductionActions: 0);
        ControlledUpdateReviewSourcePolicy.RequireActivationReadinessResult(review, result);
        return result;
    }

    public async Task<ControlledUpdateActivationValidationResult> ValidateActivationTransactionAsync(
        ControlledUpdateReview review,
        ControlledUpdateActivationReadiness readiness,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved)
        {
            _ = await stager.ExecuteActivationTransactionAsync(
                    review,
                    readiness,
                    CurrentVersion,
                    stagingRoot,
                    Now.AddMinutes(2),
                    explicitlyApproved: false,
                    new FixtureActivationValidationExecutor(),
                    cancellationToken)
                .ConfigureAwait(false);
            throw new ControlledUpdateException("update_activation_approval_required");
        }

        if (Interlocked.Exchange(ref activationValidationConsumed, 1) != 0)
        {
            throw new ControlledUpdateException("update_activation_validation_replayed");
        }

        var execution = await stager.ExecuteActivationTransactionAsync(
                review,
                readiness,
                CurrentVersion,
                stagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                new FixtureActivationValidationExecutor(),
                cancellationToken)
            .ConfigureAwait(false);
        var result = new ControlledUpdateActivationValidationResult(
            review.ReviewSha256,
            readiness.ReadinessSha256,
            review.CandidateVersion,
            execution.Status,
            execution.PackageManagerInvocations,
            execution.IsRegistered,
            execution.RestartRequired,
            transport.ManifestRequests,
            transport.PackageRequests,
            NetworkRequests: 0,
            execution.InstallerProcessesStarted,
            ElevationRequests: 0,
            ActivationAttempts: 0,
            RestartAttempts: 0,
            ProductionActions: 0);
        ControlledUpdateReviewSourcePolicy.RequireActivationValidationResult(
            review,
            readiness,
            result);
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

    private sealed class FixtureActivationValidationExecutor
        : IControlledUpdateActivationExecutor
    {
        public Task<ControlledUpdateActivationExecutionResult> ExecuteAsync(
            ControlledUpdateActivationContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ControlledUpdateActivationExecutionResult(
                ControlledUpdateActivationExecutionStatus.ValidationNoOp,
                PackageManagerInvocations: 0,
                IsRegistered: false,
                RestartRequired: false,
                RequestsElevation: false,
                InstallerProcessesStarted: 0));
        }
    }
}
