using System.Security.Cryptography;

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

public sealed class ControlledUpdateStager(
    IControlledUpdateTransport transport,
    IUpdatePackageTrustVerifier packageTrustVerifier,
    byte[] manifestSigningPublicKey)
{
    private readonly byte[] manifestSigningPublicKey =
        manifestSigningPublicKey?.ToArray() ?? throw new ArgumentNullException(nameof(manifestSigningPublicKey));

    public async Task<ControlledUpdateAdmission> CheckAndStageAsync(
        Uri manifestUri,
        string currentVersion,
        string stagingRoot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(packageTrustVerifier);
        ValidateManifestUri(manifestUri);
        var installedVersion = ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion);
        if (now.Offset != TimeSpan.Zero)
        {
            throw new ControlledUpdateException("update_time_invalid");
        }

        var envelopeBytes = await transport.DownloadManifestAsync(
                manifestUri,
                ControlledUpdateManifestCodec.MaximumEnvelopeBytes,
                cancellationToken)
            .ConfigureAwait(false);
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

        var root = ValidateStagingRoot(stagingRoot);
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        RejectReparsePoint(packages);
        var stem = $"{manifest.Version}-{manifest.PackageSha256}";
        var packagePath = Path.Combine(packages, stem + ".msix");
        var signedManifestPath = Path.Combine(packages, stem + ".manifest.json");

        if (File.Exists(packagePath))
        {
            RejectReparsePoint(packagePath);
            await VerifyPackageAsync(packagePath, manifest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var temporary = Path.Combine(packages, $".{stem}.{Guid.NewGuid():N}.tmp.msix");
            try
            {
                long downloaded;
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    downloaded = await transport.DownloadPackageAsync(
                            packageUri,
                            output,
                            manifest.PackageLength,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (downloaded != manifest.PackageLength)
                {
                    throw new ControlledUpdateException("update_package_integrity_invalid");
                }

                await VerifyPackageAsync(temporary, manifest, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, packagePath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        await WriteAtomicAsync(signedManifestPath, envelopeBytes, cancellationToken).ConfigureAwait(false);
        return new ControlledUpdateAdmission(
            manifest.Version,
            packagePath,
            signedManifestPath,
            manifest.PackageSha256,
            manifest.PackageLength);
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

    private static string ValidateStagingRoot(string path)
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

        Directory.CreateDirectory(full);
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

    private static void ValidateManifestUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlledUpdateException("update_manifest_uri_invalid");
        }
    }

    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;
}
