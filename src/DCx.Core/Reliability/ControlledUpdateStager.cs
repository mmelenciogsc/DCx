using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.Reliability;

public sealed record UpdatePackageTrustEvidence(
    bool SignatureValid,
    string PackageIdentityName,
    string PublisherId,
    string Version);

public interface IUpdatePackageTrustVerifier
{
    Task<UpdatePackageTrustEvidence> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken);
}

public sealed record ControlledUpdateAdmission(
    string Version,
    string PackagePath,
    string SignedManifestPath,
    string PackageSha256,
    long PackageLength);

public sealed record ControlledUpdateReview(
    string ReviewSha256,
    string ManifestUri,
    string CurrentVersion,
    string CandidateVersion,
    DateTimeOffset PublishedUtc,
    DateTimeOffset ExpiresUtc,
    string PackageUri,
    long PackageLength,
    string PackageSha256,
    string PackageIdentityName,
    string PublisherId,
    string EnvelopeSha256);

public sealed record ControlledUpdateActivationReadiness(
    string ReadinessSha256,
    string ReviewSha256,
    DateTimeOffset RevalidatedUtc,
    string Version,
    string PackageSha256,
    string EnvelopeSha256,
    long PackageLength,
    bool ExecutionPermitted);

public static class ControlledUpdateReviewPolicy
{
    private const string Domain = "dcx-controlled-update-review-v1";

    public static void Require(ControlledUpdateReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        _ = ControlledUpdateManifestCodec.ParsePackageVersion(review.CurrentVersion);
        var manifestUri = ControlledUpdateStager.ParseManifestUri(review.ManifestUri);
        var manifest = new ControlledUpdateManifest(
            ControlledUpdateManifestCodec.CurrentSchemaVersion,
            ControlledUpdateManifestCodec.ProductId,
            review.CandidateVersion,
            review.PublishedUtc,
            review.ExpiresUtc,
            review.PackageUri,
            review.PackageLength,
            review.PackageSha256,
            review.PackageIdentityName,
            review.PublisherId);
        ControlledUpdateManifestCodec.Validate(manifest);
        if (ControlledUpdateManifestCodec.ParsePackageVersion(review.CandidateVersion) <=
            ControlledUpdateManifestCodec.ParsePackageVersion(review.CurrentVersion) ||
            !ControlledUpdateStager.SameOrigin(
                manifestUri,
                ControlledUpdateManifestCodec.ParsePackageUri(review.PackageUri)) ||
            !IsLowerSha256(review.EnvelopeSha256) ||
            !IsLowerSha256(review.ReviewSha256) ||
            !string.Equals(review.ReviewSha256, ComputeDigest(review), StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_review_invalid");
        }
    }

    internal static ControlledUpdateReview Create(
        Uri manifestUri,
        string currentVersion,
        ControlledUpdateManifest manifest,
        string envelopeSha256)
    {
        var provisional = new ControlledUpdateReview(
            string.Empty,
            manifestUri.AbsoluteUri,
            currentVersion,
            manifest.Version,
            manifest.PublishedUtc,
            manifest.ExpiresUtc,
            manifest.PackageUri,
            manifest.PackageLength,
            manifest.PackageSha256,
            manifest.PackageIdentityName,
            manifest.PublisherId,
            envelopeSha256);
        var review = provisional with { ReviewSha256 = ComputeDigest(provisional) };
        Require(review);
        return review;
    }

    private static string ComputeDigest(ControlledUpdateReview review)
    {
        var canonical = string.Join('\n',
            Domain,
            review.ManifestUri,
            review.CurrentVersion,
            review.CandidateVersion,
            review.PublishedUtc.ToString("O", CultureInfo.InvariantCulture),
            review.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
            review.PackageUri,
            review.PackageLength.ToString(CultureInfo.InvariantCulture),
            review.PackageSha256,
            review.PackageIdentityName,
            review.PublisherId,
            review.EnvelopeSha256);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class ControlledUpdateActivationReadinessPolicy
{
    private const string Domain = "dcx-controlled-update-activation-readiness-v1";

    public static void Require(
        ControlledUpdateReview review,
        ControlledUpdateActivationReadiness readiness)
    {
        ControlledUpdateReviewPolicy.Require(review);
        ArgumentNullException.ThrowIfNull(readiness);
        if (readiness.RevalidatedUtc == default ||
            readiness.RevalidatedUtc.Offset != TimeSpan.Zero ||
            readiness.RevalidatedUtc < review.PublishedUtc.AddMinutes(-5) ||
            readiness.RevalidatedUtc >= review.ExpiresUtc ||
            !string.Equals(readiness.ReviewSha256, review.ReviewSha256, StringComparison.Ordinal) ||
            !string.Equals(readiness.Version, review.CandidateVersion, StringComparison.Ordinal) ||
            !string.Equals(readiness.PackageSha256, review.PackageSha256, StringComparison.Ordinal) ||
            !string.Equals(readiness.EnvelopeSha256, review.EnvelopeSha256, StringComparison.Ordinal) ||
            readiness.PackageLength != review.PackageLength ||
            readiness.ExecutionPermitted ||
            !IsLowerSha256(readiness.ReadinessSha256) ||
            !string.Equals(
                readiness.ReadinessSha256,
                ComputeDigest(readiness),
                StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_activation_readiness_invalid");
        }
    }

    internal static ControlledUpdateActivationReadiness Create(
        ControlledUpdateReview review,
        DateTimeOffset revalidatedUtc)
    {
        var provisional = new ControlledUpdateActivationReadiness(
            string.Empty,
            review.ReviewSha256,
            revalidatedUtc,
            review.CandidateVersion,
            review.PackageSha256,
            review.EnvelopeSha256,
            review.PackageLength,
            ExecutionPermitted: false);
        var readiness = provisional with { ReadinessSha256 = ComputeDigest(provisional) };
        Require(review, readiness);
        return readiness;
    }

    private static string ComputeDigest(ControlledUpdateActivationReadiness readiness)
    {
        var canonical = string.Join('\n',
            Domain,
            readiness.ReviewSha256,
            readiness.RevalidatedUtc.ToString("O", CultureInfo.InvariantCulture),
            readiness.Version,
            readiness.PackageSha256,
            readiness.EnvelopeSha256,
            readiness.PackageLength.ToString(CultureInfo.InvariantCulture),
            readiness.ExecutionPermitted ? "true" : "false");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ControlledUpdateStager(
    IControlledUpdateTransport transport,
    IUpdatePackageTrustVerifier packageTrustVerifier,
    byte[] manifestSigningPublicKey)
{
    private readonly byte[] manifestSigningPublicKey =
        manifestSigningPublicKey?.ToArray() ?? throw new ArgumentNullException(nameof(manifestSigningPublicKey));

    public async Task<ControlledUpdateReview> ReviewAsync(
        Uri manifestUri,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var downloaded = await DownloadAndReviewAsync(
                manifestUri,
                currentVersion,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return downloaded.Review;
    }

    public async Task<ControlledUpdateAdmission> StageReviewedAsync(
        ControlledUpdateReview review,
        string currentVersion,
        string stagingRoot,
        DateTimeOffset now,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        ControlledUpdateReviewPolicy.Require(review);
        _ = ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion);
        if (!string.Equals(currentVersion, review.CurrentVersion, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_review_changed");
        }

        if (!explicitlyApproved)
        {
            throw new ControlledUpdateException("update_approval_required");
        }

        var downloaded = await DownloadAndReviewAsync(
                ParseManifestUri(review.ManifestUri),
                currentVersion,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (downloaded.Review != review)
        {
            throw new ControlledUpdateException("update_review_changed");
        }

        return await StageAsync(downloaded, stagingRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlledUpdateActivationReadiness> PrepareActivationReadinessAsync(
        ControlledUpdateReview review,
        string currentVersion,
        string stagingRoot,
        DateTimeOffset now,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        ControlledUpdateReviewPolicy.Require(review);
        _ = ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion);
        if (!string.Equals(currentVersion, review.CurrentVersion, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_review_changed");
        }

        if (!explicitlyApproved)
        {
            throw new ControlledUpdateException("update_activation_review_approval_required");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(packageTrustVerifier);
        var root = ValidateStagingRoot(stagingRoot, createIfMissing: false);
        var packages = Path.Combine(root, "packages");
        if (!Directory.Exists(packages))
        {
            throw new ControlledUpdateException("update_staged_set_invalid");
        }

        RejectReparsePoint(packages);
        var publishedDirectory = Path.Combine(
            packages,
            $"{review.CandidateVersion}-{review.PackageSha256}");
        if (!Directory.Exists(publishedDirectory) || File.Exists(publishedDirectory))
        {
            throw new ControlledUpdateException("update_staged_set_invalid");
        }

        var packagePath = Path.Combine(publishedDirectory, "package.msix");
        var signedManifestPath = Path.Combine(publishedDirectory, "manifest.json");
        RequireExactSetEntries(publishedDirectory, packagePath, signedManifestPath);
        var envelopeBytes = await ReadEnvelopeAsync(signedManifestPath, cancellationToken)
            .ConfigureAwait(false);
        var downloaded = ReviewEnvelope(
            ParseManifestUri(review.ManifestUri),
            currentVersion,
            now,
            envelopeBytes);
        if (downloaded.Review != review)
        {
            throw new ControlledUpdateException("update_review_changed");
        }

        _ = await VerifyPublishedSetAsync(downloaded, publishedDirectory, cancellationToken)
            .ConfigureAwait(false);
        var readiness = ControlledUpdateActivationReadinessPolicy.Create(review, now);
        ControlledUpdateActivationReadinessPolicy.Require(review, readiness);
        return readiness;
    }

    private async Task<DownloadedReview> DownloadAndReviewAsync(
        Uri manifestUri,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(packageTrustVerifier);
        ArgumentNullException.ThrowIfNull(manifestUri);
        var envelopeBytes = await transport.DownloadManifestAsync(
                manifestUri,
                ControlledUpdateManifestCodec.MaximumEnvelopeBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return ReviewEnvelope(manifestUri, currentVersion, now, envelopeBytes);
    }

    private DownloadedReview ReviewEnvelope(
        Uri manifestUri,
        string currentVersion,
        DateTimeOffset now,
        byte[] envelopeBytes)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(envelopeBytes);
        _ = ParseManifestUri(manifestUri.AbsoluteUri);
        var installedVersion = ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion);
        if (now.Offset != TimeSpan.Zero)
        {
            throw new ControlledUpdateException("update_time_invalid");
        }

        var update = ControlledUpdateManifestCodec.Decode(envelopeBytes);
        ControlledUpdateManifestCodec.Verify(update, manifestSigningPublicKey);
        var manifest = update.Manifest;
        var candidateVersion = ControlledUpdateManifestCodec.ParsePackageVersion(manifest.Version);
        if (candidateVersion <= installedVersion)
        {
            throw new ControlledUpdateException("update_not_newer");
        }

        if (manifest.PublishedUtc > now.AddMinutes(5))
        {
            throw new ControlledUpdateException("update_not_yet_valid");
        }

        if (manifest.ExpiresUtc <= now)
        {
            throw new ControlledUpdateException("update_manifest_expired");
        }

        var packageUri = ControlledUpdateManifestCodec.ParsePackageUri(manifest.PackageUri);
        if (!SameOrigin(manifestUri, packageUri))
        {
            throw new ControlledUpdateException("update_origin_invalid");
        }

        var envelopeSha256 = Convert.ToHexStringLower(SHA256.HashData(envelopeBytes));
        var review = ControlledUpdateReviewPolicy.Create(
            manifestUri,
            currentVersion,
            manifest,
            envelopeSha256);
        return new DownloadedReview(review, manifest, envelopeBytes);
    }

    private static async Task<byte[]> ReadEnvelopeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is <= 0 or > ControlledUpdateManifestCodec.MaximumEnvelopeBytes)
        {
            throw new ControlledUpdateException("update_envelope_invalid");
        }

        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private async Task<ControlledUpdateAdmission> StageAsync(
        DownloadedReview downloaded,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var manifest = downloaded.Manifest;
        var packageUri = ControlledUpdateManifestCodec.ParsePackageUri(manifest.PackageUri);
        var root = ValidateStagingRoot(stagingRoot);
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        RejectReparsePoint(packages);
        var stem = $"{manifest.Version}-{manifest.PackageSha256}";
        var publishedDirectory = Path.Combine(packages, stem);
        var packagePath = Path.Combine(publishedDirectory, "package.msix");
        var signedManifestPath = Path.Combine(publishedDirectory, "manifest.json");

        if (File.Exists(publishedDirectory))
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }

        if (Directory.Exists(publishedDirectory))
        {
            return await VerifyPublishedSetAsync(
                    downloaded,
                    publishedDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var incomingDirectory = Path.Combine(packages, $".incoming-{Guid.NewGuid():N}");
        var incomingPackage = Path.Combine(incomingDirectory, "package.msix");
        var incomingManifest = Path.Combine(incomingDirectory, "manifest.json");
        try
        {
            Directory.CreateDirectory(incomingDirectory);
            RejectReparsePoint(incomingDirectory);
            long downloadedBytes;
            await using (var output = new FileStream(
                             incomingPackage,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                downloadedBytes = await transport.DownloadPackageAsync(
                        packageUri,
                        output,
                        manifest.PackageLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (downloadedBytes != manifest.PackageLength)
            {
                throw new ControlledUpdateException("update_package_integrity_invalid");
            }

            await VerifyPackageAsync(incomingPackage, manifest, cancellationToken)
                .ConfigureAwait(false);
            await WriteAtomicAsync(
                    incomingManifest,
                    downloaded.EnvelopeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyEnvelopeAsync(
                    incomingManifest,
                    downloaded.EnvelopeBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                Directory.Move(incomingDirectory, publishedDirectory);
            }
            catch (IOException)
            {
                if (!Directory.Exists(publishedDirectory))
                {
                    throw new ControlledUpdateException("update_storage_invalid");
                }

                return await VerifyPublishedSetAsync(
                        downloaded,
                        publishedDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await VerifyPublishedSetAsync(
                    downloaded,
                    publishedDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CleanupIncomingDirectory(incomingDirectory);
        }
    }

    private async Task<ControlledUpdateAdmission> VerifyPublishedSetAsync(
        DownloadedReview downloaded,
        string publishedDirectory,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(publishedDirectory);
        var packagePath = Path.Combine(publishedDirectory, "package.msix");
        var signedManifestPath = Path.Combine(publishedDirectory, "manifest.json");
        RequireExactSetEntries(publishedDirectory, packagePath, signedManifestPath);
        await VerifyPackageAsync(packagePath, downloaded.Manifest, cancellationToken)
            .ConfigureAwait(false);
        await VerifyEnvelopeAsync(
                signedManifestPath,
                downloaded.EnvelopeBytes,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExactSetEntries(publishedDirectory, packagePath, signedManifestPath);
        return new ControlledUpdateAdmission(
            downloaded.Manifest.Version,
            packagePath,
            signedManifestPath,
            downloaded.Manifest.PackageSha256,
            downloaded.Manifest.PackageLength);
    }

    private static void RequireExactSetEntries(
        string publishedDirectory,
        string packagePath,
        string signedManifestPath)
    {
        if (!File.Exists(packagePath) || !File.Exists(signedManifestPath) ||
            Directory.Exists(packagePath) || Directory.Exists(signedManifestPath))
        {
            throw new ControlledUpdateException("update_staged_set_invalid");
        }

        RejectReparsePoint(packagePath);
        RejectReparsePoint(signedManifestPath);
        var entries = Directory.EnumerateFileSystemEntries(publishedDirectory).ToArray();
        if (entries.Length != 2 ||
            !entries.Contains(packagePath, StringComparer.OrdinalIgnoreCase) ||
            !entries.Contains(signedManifestPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new ControlledUpdateException("update_staged_set_invalid");
        }
    }

    private static async Task VerifyEnvelopeAsync(
        string path,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expected.LongLength)
        {
            throw new ControlledUpdateException("update_staged_manifest_invalid");
        }

        var actualDigest = await SHA256.HashDataAsync(input, cancellationToken)
            .ConfigureAwait(false);
        var expectedDigest = SHA256.HashData(expected);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
        {
            throw new ControlledUpdateException("update_staged_manifest_invalid");
        }
    }

    private static void CleanupIncomingDirectory(string incomingDirectory)
    {
        if (!Directory.Exists(incomingDirectory)) return;
        try
        {
            RejectReparsePoint(incomingDirectory);
            foreach (var name in new[] { "package.msix", "manifest.json" })
            {
                var path = Path.Combine(incomingDirectory, name);
                if (!File.Exists(path)) continue;
                RejectReparsePoint(path);
                File.Delete(path);
            }

            if (Directory.EnumerateFileSystemEntries(incomingDirectory).Any())
            {
                throw new ControlledUpdateException("update_storage_invalid");
            }

            Directory.Delete(incomingDirectory);
        }
        catch (ControlledUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }
    }

    private async Task VerifyPackageAsync(
        string packagePath,
        ControlledUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(packagePath);
        await using var input = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != manifest.PackageLength)
        {
            throw new ControlledUpdateException("update_package_integrity_invalid");
        }

        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)
            .ConfigureAwait(false));
        if (!string.Equals(digest, manifest.PackageSha256, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_package_integrity_invalid");
        }

        var evidence = await packageTrustVerifier.VerifyAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        if (evidence is null || !evidence.SignatureValid ||
            !string.Equals(evidence.PackageIdentityName, manifest.PackageIdentityName, StringComparison.Ordinal) ||
            !string.Equals(evidence.PublisherId, manifest.PublisherId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Version, manifest.Version, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_package_trust_invalid");
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path)) RejectReparsePoint(path);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ValidateStagingRoot(string path, bool createIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }

        var full = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volumeRoot) ||
            string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
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

        if (!Directory.Exists(full))
        {
            if (!createIfMissing)
            {
                throw new ControlledUpdateException("update_staged_set_invalid");
            }

            Directory.CreateDirectory(full);
        }

        RejectReparsePoint(full);
        return full;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ControlledUpdateException("update_storage_invalid");
        }
    }

    internal static Uri ParseManifestUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ControlledUpdateException("update_manifest_uri_invalid");
        }

        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlledUpdateException("update_manifest_uri_invalid");
        }

        return uri;
    }

    internal static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    private sealed record DownloadedReview(
        ControlledUpdateReview Review,
        ControlledUpdateManifest Manifest,
        byte[] EnvelopeBytes);
}
