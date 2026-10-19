using System.Security.Cryptography;

namespace DCx.Core.Reliability;

public sealed record ControlledUpdateActivationContext(
    string PackagePath,
    string Version,
    string PackageSha256,
    long PackageLength,
    string ReviewSha256,
    string ReadinessSha256);

public enum ControlledUpdateActivationExecutionStatus
{
    ValidationNoOp,
    Registered,
    RegistrationDeferred,
}

public sealed record ControlledUpdateActivationExecutionResult(
    ControlledUpdateActivationExecutionStatus Status,
    int PackageManagerInvocations,
    bool IsRegistered,
    bool ApplicationCloseRequired,
    bool ApplicationRelaunchRequired,
    bool RestartRequired,
    bool RequestsElevation,
    int InstallerProcessesStarted);

public static class ControlledUpdateActivationExecutionPolicy
{
    public static void Require(ControlledUpdateActivationExecutionResult? result)
    {
        if (result is null || result.RequestsElevation ||
            result.InstallerProcessesStarted != 0 ||
            result.PackageManagerInvocations is < 0 or > 1)
        {
            throw new ControlledUpdateException("update_activation_result_invalid");
        }

        var valid = result.Status switch
        {
            ControlledUpdateActivationExecutionStatus.ValidationNoOp =>
                result.PackageManagerInvocations == 0 && !result.IsRegistered &&
                !result.ApplicationCloseRequired && !result.ApplicationRelaunchRequired &&
                !result.RestartRequired,
            ControlledUpdateActivationExecutionStatus.Registered =>
                result.PackageManagerInvocations == 1 && result.IsRegistered &&
                result.ApplicationCloseRequired && result.ApplicationRelaunchRequired &&
                !result.RestartRequired,
            ControlledUpdateActivationExecutionStatus.RegistrationDeferred =>
                result.PackageManagerInvocations == 1 && !result.IsRegistered &&
                result.ApplicationCloseRequired && result.ApplicationRelaunchRequired &&
                result.RestartRequired,
            _ => false,
        };
        if (!valid)
        {
            throw new ControlledUpdateException("update_activation_result_invalid");
        }
    }
}

/// <summary>
/// Product-owned activation adapters implement this contract. An implementation must not
/// return until its operating-system package operation is terminal. DCx currently composes
/// no production implementation of this interface.
/// </summary>
public interface IControlledUpdateActivationExecutor
{
    Task<ControlledUpdateActivationExecutionResult> ExecuteAsync(
        ControlledUpdateActivationContext context,
        CancellationToken cancellationToken);
}

public sealed partial class ControlledUpdateStager
{
    /// <summary>
    /// Runs a separately approved activation adapter while holding read/no-write/no-delete
    /// leases over the exact staged package and signed manifest. This method does not grant
    /// authority by itself; authority remains with the explicitly composed executor and its
    /// caller. Normal DCx composition does not expose or call this method.
    /// </summary>
    public async Task<ControlledUpdateActivationExecutionResult> ExecuteActivationTransactionAsync(
        ControlledUpdateReview review,
        ControlledUpdateActivationReadiness readiness,
        string currentVersion,
        string stagingRoot,
        DateTimeOffset now,
        bool explicitlyApproved,
        IControlledUpdateActivationExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ControlledUpdateReviewPolicy.Require(review);
        ControlledUpdateActivationReadinessPolicy.Require(review, readiness);
        ArgumentNullException.ThrowIfNull(executor);
        _ = ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion);
        if (!string.Equals(currentVersion, review.CurrentVersion, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_review_changed");
        }

        if (!explicitlyApproved)
        {
            throw new ControlledUpdateException("update_activation_approval_required");
        }

        if (now.Offset != TimeSpan.Zero || now < readiness.RevalidatedUtc)
        {
            throw new ControlledUpdateException("update_activation_time_invalid");
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

        FileStream? packageLease = null;
        FileStream? manifestLease = null;
        try
        {
            packageLease = OpenActivationLease(packagePath, 64 * 1024);
            manifestLease = OpenActivationLease(signedManifestPath, 16 * 1024);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            packageLease?.Dispose();
            manifestLease?.Dispose();
            throw new ControlledUpdateException("update_activation_lease_unavailable");
        }
        catch
        {
            packageLease?.Dispose();
            manifestLease?.Dispose();
            throw;
        }

        await using (packageLease)
        await using (manifestLease)
        {
            await RevalidateLeasedSetAsync(
                    review,
                    currentVersion,
                    now,
                    publishedDirectory,
                    packagePath,
                    signedManifestPath,
                    packageLease,
                    manifestLease,
                    cancellationToken)
                .ConfigureAwait(false);

            var context = new ControlledUpdateActivationContext(
                packagePath,
                review.CandidateVersion,
                review.PackageSha256,
                review.PackageLength,
                review.ReviewSha256,
                readiness.ReadinessSha256);

            ControlledUpdateActivationExecutionResult? executionResult;
            try
            {
                executionResult = await executor.ExecuteAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RevalidateLeasedSetAsync(
                        review,
                        currentVersion,
                        now,
                        publishedDirectory,
                        packagePath,
                        signedManifestPath,
                        packageLease,
                        manifestLease,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await RevalidateLeasedSetAsync(
                        review,
                        currentVersion,
                        now,
                        publishedDirectory,
                        packagePath,
                        signedManifestPath,
                        packageLease,
                        manifestLease,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw new ControlledUpdateException("update_activation_executor_failed");
            }

            await RevalidateLeasedSetAsync(
                    review,
                    currentVersion,
                    now,
                    publishedDirectory,
                    packagePath,
                    signedManifestPath,
                    packageLease,
                    manifestLease,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ControlledUpdateActivationExecutionPolicy.Require(executionResult);
            return executionResult!;
        }
    }

    private static FileStream OpenActivationLease(string path, int bufferSize)
    {
        RejectReparsePoint(path);
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task RevalidateLeasedSetAsync(
        ControlledUpdateReview review,
        string currentVersion,
        DateTimeOffset now,
        string publishedDirectory,
        string packagePath,
        string signedManifestPath,
        FileStream packageLease,
        FileStream manifestLease,
        CancellationToken cancellationToken)
    {
        RequireExactSetEntries(publishedDirectory, packagePath, signedManifestPath);
        var envelopeBytes = await ReadLeasedEnvelopeAsync(manifestLease, cancellationToken)
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

        await VerifyLeasedPackageAsync(
                packageLease,
                packagePath,
                downloaded.Manifest,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExactSetEntries(publishedDirectory, packagePath, signedManifestPath);
    }

    private static async Task<byte[]> ReadLeasedEnvelopeAsync(
        FileStream manifestLease,
        CancellationToken cancellationToken)
    {
        if (!manifestLease.CanRead || !manifestLease.CanSeek ||
            manifestLease.Length is <= 0 or > ControlledUpdateManifestCodec.MaximumEnvelopeBytes)
        {
            throw new ControlledUpdateException("update_envelope_invalid");
        }

        manifestLease.Position = 0;
        var bytes = new byte[checked((int)manifestLease.Length)];
        await manifestLease.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        manifestLease.Position = 0;
        return bytes;
    }

    private async Task VerifyLeasedPackageAsync(
        FileStream packageLease,
        string packagePath,
        ControlledUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        await RequireLeasedPackageDigestAsync(packageLease, manifest, cancellationToken)
            .ConfigureAwait(false);
        var evidence = await packageTrustVerifier.VerifyAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        if (evidence is null || !evidence.SignatureValid ||
            !string.Equals(evidence.PackageIdentityName, manifest.PackageIdentityName, StringComparison.Ordinal) ||
            !string.Equals(evidence.PublisherId, manifest.PublisherId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Version, manifest.Version, StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_package_trust_invalid");
        }

        await RequireLeasedPackageDigestAsync(packageLease, manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RequireLeasedPackageDigestAsync(
        FileStream packageLease,
        ControlledUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!packageLease.CanRead || !packageLease.CanSeek ||
            packageLease.Length != manifest.PackageLength)
        {
            throw new ControlledUpdateException("update_package_integrity_invalid");
        }

        packageLease.Position = 0;
        var digest = await SHA256.HashDataAsync(packageLease, cancellationToken)
            .ConfigureAwait(false);
        packageLease.Position = 0;
        var expected = Convert.FromHexString(manifest.PackageSha256);
        if (!CryptographicOperations.FixedTimeEquals(digest, expected))
        {
            throw new ControlledUpdateException("update_package_integrity_invalid");
        }
    }
}
