using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class ControlledUpdateStagerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    private static readonly Uri ManifestUri =
        new("https://updates.example.test/stable/manifest.json");
    private static readonly Uri PackageUri =
        new("https://updates.example.test/stable/DCx-1.2.0.0.msix");

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-controlled-update-{Guid.NewGuid():N}");

    [Fact]
    public async Task SignedPackage_IsAdmittedOnceAndReverifiedWithoutInstallation()
    {
        var package = "synthetic signed MSIX fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, package);
        var transport = new FixtureTransport(ManifestUri, envelope, PackageUri, package);
        var trust = new FixtureTrustVerifier(ValidTrust());
        var stager = new ControlledUpdateStager(
            transport, trust, key.ExportSubjectPublicKeyInfo());

        var review = await stager.ReviewAsync(ManifestUri, "1.1.9.9", Now);
        Assert.Equal(0, transport.PackageDownloads);
        await AssertCodeAsync("update_approval_required", () => stager.StageReviewedAsync(
            review, "1.1.9.9", root, Now, explicitlyApproved: false));
        Assert.Equal(1, transport.ManifestDownloads);
        Assert.Equal(0, transport.PackageDownloads);

        var first = await stager.StageReviewedAsync(
            review, "1.1.9.9", root, Now, explicitlyApproved: true);
        var second = await stager.StageReviewedAsync(
            review, "1.1.9.9", root, Now, explicitlyApproved: true);

        Assert.Equal("1.2.0.0", first.Version);
        Assert.Equal(first, second);
        Assert.Equal(package, await File.ReadAllBytesAsync(first.PackagePath));
        Assert.Equal(envelope, await File.ReadAllBytesAsync(first.SignedManifestPath));
        Assert.Equal(3, transport.ManifestDownloads);
        Assert.Equal(1, transport.PackageDownloads);
        Assert.Equal(3, trust.Verifications);
        Assert.Equal(
            ["manifest.json", "package.msix"],
            Directory.EnumerateFiles(Path.GetDirectoryName(first.PackagePath)!)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain("installed", first.PackagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedTransfer_DoesNotPublishIncompleteUpdateSet()
    {
        var package = "atomic update package fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = new FixtureTransport(
            ManifestUri,
            Envelope(key, package),
            PackageUri,
            package)
        {
            FailAfterPartialPackage = true,
        };
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var stagingRoot = Path.Combine(root, "partial-transfer");

        await AssertCodeAsync("fixture_package_transfer_failed", () =>
            stager.StageReviewedAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now,
                explicitlyApproved: true));

        var packages = Path.Combine(stagingRoot, "packages");
        Assert.True(Directory.Exists(packages));
        Assert.Empty(Directory.EnumerateFileSystemEntries(packages));
        transport.FailAfterPartialPackage = false;

        var admission = await stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now,
            explicitlyApproved: true);

        Assert.Equal(package, await File.ReadAllBytesAsync(admission.PackagePath));
        Assert.Equal(
            ["manifest.json", "package.msix"],
            Directory.EnumerateFiles(Path.GetDirectoryName(admission.PackagePath)!)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task PublishedSet_RejectsMissingExtraAndChangedManifestWithoutRedownload()
    {
        var package = "complete atomic set fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, package);
        var transport = new FixtureTransport(ManifestUri, envelope, PackageUri, package);
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var stagingRoot = Path.Combine(root, "published-set");
        var admission = await stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now,
            explicitlyApproved: true);
        var packageDownloads = transport.PackageDownloads;

        File.Delete(admission.SignedManifestPath);
        await AssertCodeAsync("update_staged_set_invalid", () =>
            stager.StageReviewedAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now,
                explicitlyApproved: true));
        Assert.Equal(packageDownloads, transport.PackageDownloads);

        await File.WriteAllBytesAsync(admission.SignedManifestPath, envelope);
        var extra = Path.Combine(Path.GetDirectoryName(admission.PackagePath)!, "unexpected.txt");
        await File.WriteAllTextAsync(extra, "not part of the staged set");
        await AssertCodeAsync("update_staged_set_invalid", () =>
            stager.StageReviewedAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now,
                explicitlyApproved: true));
        File.Delete(extra);

        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var changedEnvelope = Envelope(wrongKey, package);
        await File.WriteAllBytesAsync(admission.SignedManifestPath, changedEnvelope);
        await AssertCodeAsync("update_staged_manifest_invalid", () =>
            stager.StageReviewedAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now,
                explicitlyApproved: true));
        Assert.Equal(packageDownloads, transport.PackageDownloads);
    }

    [Fact]
    public async Task ConcurrentStages_PublishOneCompleteSet()
    {
        var package = new byte[128 * 1024];
        RandomNumberGenerator.Fill(package);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, package);
        var transport = new FixtureTransport(ManifestUri, envelope, PackageUri, package);
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var stagingRoot = Path.Combine(root, "concurrent");

        var admissions = await Task.WhenAll(
            stager.StageReviewedAsync(
                review, "1.1.0.0", stagingRoot, Now, explicitlyApproved: true),
            stager.StageReviewedAsync(
                review, "1.1.0.0", stagingRoot, Now, explicitlyApproved: true));

        Assert.Equal(admissions[0], admissions[1]);
        var packages = Path.Combine(stagingRoot, "packages");
        Assert.Single(Directory.EnumerateDirectories(packages));
        Assert.Empty(Directory.EnumerateFiles(packages));
        Assert.Equal(
            ["manifest.json", "package.msix"],
            Directory.EnumerateFiles(Path.GetDirectoryName(admissions[0].PackagePath)!)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(packages),
            path => Path.GetFileName(path).StartsWith(".incoming-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivationReadiness_RevalidatesExactSetLocallyAndBindsFreshDigest()
    {
        var package = "final activation-readiness fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = new FixtureTransport(
            ManifestUri,
            Envelope(key, package),
            PackageUri,
            package);
        var trust = new FixtureTrustVerifier(ValidTrust());
        var stager = new ControlledUpdateStager(
            transport,
            trust,
            key.ExportSubjectPublicKeyInfo());
        var stagingRoot = Path.Combine(root, "activation-readiness");
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        await stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now,
            explicitlyApproved: true);
        var manifestRequests = transport.ManifestDownloads;
        var packageRequests = transport.PackageDownloads;
        var trustChecks = trust.Verifications;

        await AssertCodeAsync("update_activation_review_approval_required", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: false));
        Assert.Equal(trustChecks, trust.Verifications);

        var readiness = await stager.PrepareActivationReadinessAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now.AddMinutes(1),
            explicitlyApproved: true);

        ControlledUpdateActivationReadinessPolicy.Require(review, readiness);
        Assert.Equal(review.ReviewSha256, readiness.ReviewSha256);
        Assert.Equal(review.CandidateVersion, readiness.Version);
        Assert.Equal(review.PackageSha256, readiness.PackageSha256);
        Assert.Equal(review.EnvelopeSha256, readiness.EnvelopeSha256);
        Assert.Equal(review.PackageLength, readiness.PackageLength);
        Assert.False(readiness.ExecutionPermitted);
        Assert.Equal(manifestRequests, transport.ManifestDownloads);
        Assert.Equal(packageRequests, transport.PackageDownloads);
        Assert.Equal(trustChecks + 1, trust.Verifications);
    }

    [Fact]
    public async Task ActivationReadiness_RejectsSubstitutionExpiryCancellationAndMissingSet()
    {
        var package = "activation policy fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = new FixtureTransport(
            ManifestUri,
            Envelope(key, package),
            PackageUri,
            package);
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var stagingRoot = Path.Combine(root, "activation-policy");
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        await stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now,
            explicitlyApproved: true);

        await AssertCodeAsync("update_review_changed", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.0.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: true));
        await AssertCodeAsync("update_manifest_expired", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddDays(2),
                explicitlyApproved: true));
        await AssertCodeAsync("update_staged_set_invalid", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                Path.Combine(root, "missing-activation-set"),
                Now.AddMinutes(1),
                explicitlyApproved: true));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: true,
                cancelled.Token));
    }

    [Fact]
    public async Task ActivationReadiness_RejectsEveryChangedFinalSetMember()
    {
        var package = "activation final-set fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, package);
        var transport = new FixtureTransport(ManifestUri, envelope, PackageUri, package);
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var stagingRoot = Path.Combine(root, "activation-tamper");
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var admission = await stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            stagingRoot,
            Now,
            explicitlyApproved: true);

        var changedPackage = package.ToArray();
        changedPackage[0] ^= 1;
        await File.WriteAllBytesAsync(admission.PackagePath, changedPackage);
        await AssertCodeAsync("update_package_integrity_invalid", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: true));
        await File.WriteAllBytesAsync(admission.PackagePath, package);

        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var changedEnvelope = Envelope(wrongKey, package);
        await File.WriteAllBytesAsync(admission.SignedManifestPath, changedEnvelope);
        await AssertCodeAsync("update_signature_invalid", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: true));
        await File.WriteAllBytesAsync(admission.SignedManifestPath, envelope);

        var unexpected = Path.Combine(Path.GetDirectoryName(admission.PackagePath)!, "extra.txt");
        await File.WriteAllTextAsync(unexpected, "not admitted");
        await AssertCodeAsync("update_staged_set_invalid", () =>
            stager.PrepareActivationReadinessAsync(
                review,
                "1.1.0.0",
                stagingRoot,
                Now.AddMinutes(1),
                explicitlyApproved: true));
    }

    [Fact]
    public async Task ActivationTransaction_HoldsExactFilesAgainstMutationAndRevalidates()
    {
        var fixture = await CreateActivationFixtureAsync("activation-lease");
        var trustChecks = fixture.Trust.Verifications;
        var executor = new FixtureActivationExecutor((context, _) =>
        {
            Assert.Equal(fixture.Admission.PackagePath, context.PackagePath);
            Assert.Equal(fixture.Review.CandidateVersion, context.Version);
            Assert.Equal(fixture.Review.PackageSha256, context.PackageSha256);
            Assert.Equal(fixture.Review.PackageLength, context.PackageLength);
            Assert.Equal(fixture.Review.ReviewSha256, context.ReviewSha256);
            Assert.Equal(fixture.Readiness.ReadinessSha256, context.ReadinessSha256);
            Assert.Equal(fixture.Package, File.ReadAllBytes(context.PackagePath));

            var publishedDirectory = Path.GetDirectoryName(context.PackagePath)!;
            var manifestPath = Path.Combine(publishedDirectory, "manifest.json");
            AssertLeaseDenies(() =>
                File.Open(context.PackagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                    .Dispose());
            AssertLeaseDenies(() =>
                File.Open(manifestPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                    .Dispose());
            AssertLeaseDenies(() => File.Delete(context.PackagePath));
            AssertLeaseDenies(() => File.Delete(manifestPath));
            AssertLeaseDenies(() => Directory.Move(
                publishedDirectory,
                publishedDirectory + "-moved"));
            return Task.CompletedTask;
        });

        await fixture.Stager.ExecuteActivationTransactionAsync(
            fixture.Review,
            fixture.Readiness,
            fixture.Review.CurrentVersion,
            fixture.StagingRoot,
            Now.AddMinutes(2),
            explicitlyApproved: true,
            executor);

        Assert.Equal(1, executor.Executions);
        Assert.Equal(trustChecks + 2, fixture.Trust.Verifications);
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(fixture.Admission.PackagePath));
        Assert.Equal(
            ["manifest.json", "package.msix"],
            Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Admission.PackagePath)!)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task ActivationTransaction_RequiresSeparateApprovalExactReceiptAndCurrentVersion()
    {
        var fixture = await CreateActivationFixtureAsync("activation-authority");
        var trustChecks = fixture.Trust.Verifications;
        var executor = new FixtureActivationExecutor((_, _) => Task.CompletedTask);

        await AssertCodeAsync("update_activation_approval_required", () =>
            fixture.Stager.ExecuteActivationTransactionAsync(
                fixture.Review,
                fixture.Readiness,
                fixture.Review.CurrentVersion,
                fixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: false,
                executor));
        await AssertCodeAsync("update_activation_readiness_invalid", () =>
            fixture.Stager.ExecuteActivationTransactionAsync(
                fixture.Review,
                fixture.Readiness with { ReadinessSha256 = new string('0', 64) },
                fixture.Review.CurrentVersion,
                fixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                executor));
        await AssertCodeAsync("update_review_changed", () =>
            fixture.Stager.ExecuteActivationTransactionAsync(
                fixture.Review,
                fixture.Readiness,
                "1.0.0.1",
                fixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                executor));
        await AssertCodeAsync("update_activation_time_invalid", () =>
            fixture.Stager.ExecuteActivationTransactionAsync(
                fixture.Review,
                fixture.Readiness,
                fixture.Review.CurrentVersion,
                fixture.StagingRoot,
                fixture.Readiness.RevalidatedUtc.AddTicks(-1),
                explicitlyApproved: true,
                executor));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Stager.ExecuteActivationTransactionAsync(
                fixture.Review,
                fixture.Readiness,
                fixture.Review.CurrentVersion,
                fixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                executor,
                cancelled.Token));

        Assert.Equal(0, executor.Executions);
        Assert.Equal(trustChecks, fixture.Trust.Verifications);
    }

    [Fact]
    public async Task ActivationTransaction_RejectsExistingWriterAndPostExecutorSetDrift()
    {
        var writerFixture = await CreateActivationFixtureAsync("activation-writer");
        var writerExecutor = new FixtureActivationExecutor((_, _) => Task.CompletedTask);
        await using (var writer = new FileStream(
                         writerFixture.Admission.PackagePath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.ReadWrite))
        {
            await AssertCodeAsync("update_activation_lease_unavailable", () =>
                writerFixture.Stager.ExecuteActivationTransactionAsync(
                    writerFixture.Review,
                    writerFixture.Readiness,
                    writerFixture.Review.CurrentVersion,
                    writerFixture.StagingRoot,
                    Now.AddMinutes(2),
                    explicitlyApproved: true,
                    writerExecutor));
        }

        Assert.Equal(0, writerExecutor.Executions);

        var driftFixture = await CreateActivationFixtureAsync("activation-drift");
        var unexpectedPath = Path.Combine(
            Path.GetDirectoryName(driftFixture.Admission.PackagePath)!,
            "unexpected.txt");
        var driftExecutor = new FixtureActivationExecutor((_, _) =>
        {
            File.WriteAllText(unexpectedPath, "not in the admitted update set");
            return Task.CompletedTask;
        });

        await AssertCodeAsync("update_staged_set_invalid", () =>
            driftFixture.Stager.ExecuteActivationTransactionAsync(
                driftFixture.Review,
                driftFixture.Readiness,
                driftFixture.Review.CurrentVersion,
                driftFixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                driftExecutor));

        Assert.Equal(1, driftExecutor.Executions);
        File.Delete(unexpectedPath);
    }

    [Fact]
    public async Task ActivationTransaction_MapsExecutorFailureAndPostChecksCancellation()
    {
        var failedFixture = await CreateActivationFixtureAsync("activation-executor-failure");
        var failedTrustChecks = failedFixture.Trust.Verifications;
        var failedExecutor = new FixtureActivationExecutor((_, _) =>
            throw new InvalidOperationException("private executor detail"));

        await AssertCodeAsync("update_activation_executor_failed", () =>
            failedFixture.Stager.ExecuteActivationTransactionAsync(
                failedFixture.Review,
                failedFixture.Readiness,
                failedFixture.Review.CurrentVersion,
                failedFixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                failedExecutor));
        Assert.Equal(1, failedExecutor.Executions);
        Assert.Equal(failedTrustChecks + 2, failedFixture.Trust.Verifications);

        var cancelledFixture = await CreateActivationFixtureAsync("activation-executor-cancel");
        var cancelledTrustChecks = cancelledFixture.Trust.Verifications;
        using var cancellation = new CancellationTokenSource();
        var cancelledExecutor = new FixtureActivationExecutor((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledFixture.Stager.ExecuteActivationTransactionAsync(
                cancelledFixture.Review,
                cancelledFixture.Readiness,
                cancelledFixture.Review.CurrentVersion,
                cancelledFixture.StagingRoot,
                Now.AddMinutes(2),
                explicitlyApproved: true,
                cancelledExecutor,
                cancellation.Token));
        Assert.Equal(1, cancelledExecutor.Executions);
        Assert.Equal(cancelledTrustChecks + 2, cancelledFixture.Trust.Verifications);
    }

    [Fact]
    public async Task ManifestPolicy_RejectsWrongSignatureDowngradeExpiryAndCrossOrigin()
    {
        var package = "update fixture"u8.ToArray();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validEnvelope = Envelope(signer, package);
        var transport = new FixtureTransport(ManifestUri, validEnvelope, PackageUri, package);
        var stager = new ControlledUpdateStager(
            transport, new FixtureTrustVerifier(ValidTrust()), wrongKey.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_signature_invalid", () => stager.ReviewAsync(
            ManifestUri, "1.1.0.0", Now));
        Assert.Equal(0, transport.PackageDownloads);

        var trusted = new ControlledUpdateStager(
            transport, new FixtureTrustVerifier(ValidTrust()), signer.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_not_newer", () => trusted.ReviewAsync(
            ManifestUri, "1.2.0.0", Now));

        var expiredManifest = Manifest(package) with
        {
            PublishedUtc = Now.AddDays(-2),
            ExpiresUtc = Now.AddDays(-1),
        };
        transport.Manifest = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(expiredManifest, signer));
        await AssertCodeAsync("update_manifest_expired", () => trusted.ReviewAsync(
            ManifestUri, "1.1.0.0", Now));

        var crossOrigin = Manifest(package) with
        {
            PackageUri = "https://attacker.example.test/DCx-1.2.0.0.msix",
        };
        transport.Manifest = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(crossOrigin, signer));
        await AssertCodeAsync("update_origin_invalid", () => trusted.ReviewAsync(
            ManifestUri, "1.1.0.0", Now));
        Assert.Equal(0, transport.PackageDownloads);
    }

    [Fact]
    public async Task PackageAdmission_RejectsDigestTrustAndExistingFileTampering()
    {
        var expected = "expected package"u8.ToArray();
        var changed = "tampered package"u8.ToArray();
        Assert.Equal(expected.Length, changed.Length);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, expected);
        var badTransport = new FixtureTransport(ManifestUri, envelope, PackageUri, changed);
        var stager = new ControlledUpdateStager(
            badTransport, new FixtureTrustVerifier(ValidTrust()), key.ExportSubjectPublicKeyInfo());
        var digestReview = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        await AssertCodeAsync("update_package_integrity_invalid", () => stager.StageReviewedAsync(
            digestReview,
            "1.1.0.0",
            Path.Combine(root, "digest"),
            Now,
            explicitlyApproved: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "digest", "packages")));

        var goodTransport = new FixtureTransport(ManifestUri, envelope, PackageUri, expected);
        var invalidTrust = new FixtureTrustVerifier(ValidTrust() with { SignatureValid = false });
        var untrustedStager = new ControlledUpdateStager(
            goodTransport, invalidTrust, key.ExportSubjectPublicKeyInfo());
        var trustReview = await untrustedStager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        await AssertCodeAsync("update_package_trust_invalid", () =>
            untrustedStager.StageReviewedAsync(
                trustReview,
                "1.1.0.0",
                Path.Combine(root, "trust"),
                Now,
                explicitlyApproved: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "trust", "packages")));

        var validStager = new ControlledUpdateStager(
            goodTransport, new FixtureTrustVerifier(ValidTrust()), key.ExportSubjectPublicKeyInfo());
        var validReview = await validStager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var admitted = await validStager.StageReviewedAsync(
            validReview,
            "1.1.0.0",
            Path.Combine(root, "existing"),
            Now,
            explicitlyApproved: true);
        var downloadsBeforeTamperCheck = goodTransport.PackageDownloads;
        await File.WriteAllBytesAsync(admitted.PackagePath, changed);
        await AssertCodeAsync("update_package_integrity_invalid", () =>
            validStager.StageReviewedAsync(
                validReview,
                "1.1.0.0",
                Path.Combine(root, "existing"),
                Now,
                explicitlyApproved: true));
        Assert.Equal(downloadsBeforeTamperCheck, goodTransport.PackageDownloads);
    }

    [Fact]
    public async Task Stage_RequiresExactFreshReviewAndApprovalBeforeAnyPackageRequest()
    {
        var package = "review-bound package"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = new FixtureTransport(
            ManifestUri,
            Envelope(key, package),
            PackageUri,
            package);
        var stager = new ControlledUpdateStager(
            transport,
            new FixtureTrustVerifier(ValidTrust()),
            key.ExportSubjectPublicKeyInfo());
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);

        await AssertCodeAsync("update_review_invalid", () => stager.StageReviewedAsync(
            review with { PackageLength = review.PackageLength + 1 },
            "1.1.0.0",
            Path.Combine(root, "tampered-review"),
            Now,
            explicitlyApproved: true));
        await AssertCodeAsync("update_review_changed", () => stager.StageReviewedAsync(
            review,
            "1.0.0.0",
            Path.Combine(root, "changed-current"),
            Now,
            explicitlyApproved: true));
        await AssertCodeAsync("update_approval_required", () => stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            Path.Combine(root, "not-approved"),
            Now,
            explicitlyApproved: false));
        Assert.Equal(1, transport.ManifestDownloads);
        Assert.Equal(0, transport.PackageDownloads);
        Assert.False(Directory.Exists(Path.Combine(root, "not-approved")));

        var changed = Manifest(package) with { Version = "1.3.0.0" };
        transport.Manifest = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(changed, key));
        await AssertCodeAsync("update_review_changed", () => stager.StageReviewedAsync(
            review,
            "1.1.0.0",
            Path.Combine(root, "changed-manifest"),
            Now,
            explicitlyApproved: true));
        Assert.Equal(2, transport.ManifestDownloads);
        Assert.Equal(0, transport.PackageDownloads);
        Assert.False(Directory.Exists(Path.Combine(root, "changed-manifest")));
    }

    [Fact]
    public void Codec_RejectsNoncanonicalUnknownAndInvalidVersionManifests()
    {
        var package = "package"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var canonical = Envelope(key, package);
        var padded = new byte[canonical.Length + 1];
        padded[0] = (byte)' ';
        canonical.CopyTo(padded, 1);
        AssertCode("update_envelope_noncanonical", () =>
            ControlledUpdateManifestCodec.Decode(padded));

        var text = System.Text.Encoding.UTF8.GetString(canonical);
        var unknown = System.Text.Encoding.UTF8.GetBytes(
            text.Insert(text.Length - 1, ",\"unknown\":true"));
        AssertCode("update_envelope_invalid", () =>
            ControlledUpdateManifestCodec.Decode(unknown));
        AssertCode("update_envelope_invalid", () =>
            ControlledUpdateManifestCodec.Decode("{}"u8));

        AssertCode("update_version_invalid", () =>
            ControlledUpdateManifestCodec.ParsePackageVersion("1.2.3"));
        AssertCode("update_version_invalid", () =>
            ControlledUpdateManifestCodec.ParsePackageVersion("1.02.3.4"));
    }

    [Fact]
    public async Task HttpTransport_EnforcesOriginMediaTypeSizeAndRedirectDenial()
    {
        var handler = new FixtureHttpHandler(request =>
        {
            var content = new ByteArrayContent("{}"u8.ToArray());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                HttpControlledUpdateTransport.ManifestMediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var transport = new HttpControlledUpdateTransport(
            new Uri("https://updates.example.test/"), handler);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(
            await transport.DownloadManifestAsync(ManifestUri, 100, default)));
        Assert.Equal(1, handler.Requests);

        await AssertCodeAsync("update_origin_invalid", () => transport.DownloadManifestAsync(
            new Uri("https://other.example.test/manifest.json"), 100, default));
        Assert.Equal(1, handler.Requests);

        handler.Response = _ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.test/manifest.json") },
        };
        await AssertCodeAsync("update_transport_failed", () =>
            transport.DownloadManifestAsync(ManifestUri, 100, default));

        handler.Response = _ =>
        {
            var content = new ByteArrayContent(new byte[101]);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                HttpControlledUpdateTransport.ManifestMediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        await AssertCodeAsync("update_manifest_oversized", () =>
            transport.DownloadManifestAsync(ManifestUri, 100, default));

        var package = "http package fixture"u8.ToArray();
        handler.Response = _ =>
        {
            var content = new ByteArrayContent(package);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/msix");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        using var destination = new MemoryStream();
        Assert.Equal(package.Length, await transport.DownloadPackageAsync(
            PackageUri, destination, package.Length, default));
        Assert.Equal(package, destination.ToArray());
        await AssertCodeAsync("update_package_oversized", () => transport.DownloadPackageAsync(
            PackageUri, new MemoryStream(), package.Length - 1, default));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<ActivationFixture> CreateActivationFixtureAsync(string name)
    {
        var package = System.Text.Encoding.UTF8.GetBytes(
            $"lease-held update fixture: {name}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var transport = new FixtureTransport(
            ManifestUri,
            Envelope(key, package),
            PackageUri,
            package);
        var trust = new FixtureTrustVerifier(ValidTrust());
        var stager = new ControlledUpdateStager(
            transport,
            trust,
            key.ExportSubjectPublicKeyInfo());
        var stagingRoot = Path.Combine(root, name);
        var review = await stager.ReviewAsync(ManifestUri, "1.1.0.0", Now);
        var admission = await stager.StageReviewedAsync(
            review,
            review.CurrentVersion,
            stagingRoot,
            Now,
            explicitlyApproved: true);
        var readiness = await stager.PrepareActivationReadinessAsync(
            review,
            review.CurrentVersion,
            stagingRoot,
            Now.AddMinutes(1),
            explicitlyApproved: true);
        return new ActivationFixture(
            stager,
            trust,
            review,
            readiness,
            admission,
            stagingRoot,
            package);
    }

    private static void AssertLeaseDenies(Action action)
    {
        var exception = Record.Exception(action);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected a Windows sharing denial, but received {exception?.GetType().Name ?? "no exception"}.");
    }

    private static ControlledUpdateManifest Manifest(byte[] package) => new(
        ControlledUpdateManifestCodec.CurrentSchemaVersion,
        ControlledUpdateManifestCodec.ProductId,
        "1.2.0.0",
        Now,
        Now.AddDays(1),
        PackageUri.AbsoluteUri,
        package.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(package)),
        "DCx.AgenticBrowser",
        "CN=DCx Test Publisher");

    private static byte[] Envelope(ECDsa key, byte[] package) =>
        ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(Manifest(package), key));

    private static UpdatePackageTrustEvidence ValidTrust() => new(
        true,
        "DCx.AgenticBrowser",
        "CN=DCx Test Publisher",
        "1.2.0.0");

    private static async Task AssertCodeAsync(
        string expected,
        Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FixtureTransport(
        Uri manifestUri,
        byte[] manifest,
        Uri packageUri,
        byte[] package) : IControlledUpdateTransport
    {
        private int manifestDownloads;
        private int packageDownloads;

        public byte[] Manifest { get; set; } = manifest;
        public bool FailAfterPartialPackage { get; set; }
        public int ManifestDownloads => Volatile.Read(ref manifestDownloads);
        public int PackageDownloads => Volatile.Read(ref packageDownloads);

        public Task<byte[]> DownloadManifestAsync(
            Uri uri,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(manifestUri, uri);
            Assert.True(Manifest.Length <= maximumBytes);
            Interlocked.Increment(ref manifestDownloads);
            return Task.FromResult(Manifest.ToArray());
        }

        public async Task<long> DownloadPackageAsync(
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(packageUri, uri);
            Assert.True(package.LongLength <= maximumBytes);
            Interlocked.Increment(ref packageDownloads);
            if (FailAfterPartialPackage)
            {
                await destination.WriteAsync(
                    package.AsMemory(0, Math.Max(1, package.Length / 2)),
                    cancellationToken);
                throw new ControlledUpdateException("fixture_package_transfer_failed");
            }

            await destination.WriteAsync(package, cancellationToken);
            return package.LongLength;
        }
    }

    private sealed class FixtureTrustVerifier(UpdatePackageTrustEvidence evidence)
        : IUpdatePackageTrustVerifier
    {
        private int verifications;

        public int Verifications => Volatile.Read(ref verifications);

        public Task<UpdatePackageTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(packagePath));
            Interlocked.Increment(ref verifications);
            return Task.FromResult(evidence);
        }
    }

    private sealed class FixtureActivationExecutor(
        Func<ControlledUpdateActivationContext, CancellationToken, Task> execute)
        : IControlledUpdateActivationExecutor
    {
        private int executions;

        public int Executions => Volatile.Read(ref executions);

        public Task ExecuteAsync(
            ControlledUpdateActivationContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executions);
            return execute(context, cancellationToken);
        }
    }

    private sealed record ActivationFixture(
        ControlledUpdateStager Stager,
        FixtureTrustVerifier Trust,
        ControlledUpdateReview Review,
        ControlledUpdateActivationReadiness Readiness,
        ControlledUpdateAdmission Admission,
        string StagingRoot,
        byte[] Package);

    private sealed class FixtureHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var result = Response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }
}
