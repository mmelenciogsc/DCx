using System.Security.Cryptography;
using System.Text.Json;

namespace DCx.Core.Deployment;

public sealed class PackageInstallReviewException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record PackageInstallTrustEvidence(
    bool SignatureValid,
    string PackageIdentityName,
    string PublisherId,
    string Version);

public interface IPackageInstallTrustVerifier
{
    Task<PackageInstallTrustEvidence> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken);
}

public enum PackageInstallReviewState
{
    Pending,
    ApprovedForValidation,
    Cancelled,
    Denied,
    Disposed,
}

public sealed record PackageInstallProposal(
    int SchemaVersion,
    string OperationKind,
    string DeploymentApi,
    string DeploymentOptions,
    string Scope,
    bool RequestsElevation,
    string PackagePath,
    string PackageUri,
    string PackageFileName,
    long PackageLength,
    string PackageSha256,
    string PackageIdentityName,
    string PublisherId,
    string Version,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string ProposalSha256);

public sealed record PackageInstallApprovalReceipt(
    string ProposalSha256,
    DateTimeOffset ApprovedUtc,
    string OperationKind,
    bool ExecutionPermitted,
    int InstallerProcessesStarted);

public static class PackageInstallReviewService
{
    public const int CurrentSchemaVersion = 1;
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    public const string CurrentUserMsixOperation = "msix-current-user-install";
    public const string DeploymentApiName =
        "Windows.Management.Deployment.PackageManager.AddPackageByUriAsync";
    public const string NoDeploymentOptions = "None";
    public const string CurrentUserScope = "Current Windows user";
    public static readonly TimeSpan MaximumReviewAge = TimeSpan.FromMinutes(10);

    public static async Task<PackageInstallReviewSession> BeginAsync(
        string packagePath,
        IPackageInstallTrustVerifier trustVerifier,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustVerifier);
        ValidateUtc(now);
        var canonicalPath = ValidatePackagePath(packagePath);
        FileStream? lease = null;
        try
        {
            lease = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            RejectReparseHierarchy(canonicalPath);
            if (lease.Length is < 1 or > MaximumPackageBytes)
            {
                throw new PackageInstallReviewException("package_review_size_invalid");
            }

            var packageSha256 = await ComputeSha256Async(lease, cancellationToken).ConfigureAwait(false);
            var evidence = await VerifyTrustAsync(
                    trustVerifier,
                    canonicalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var packageUri = new Uri(canonicalPath, UriKind.Absolute).AbsoluteUri;
            var expiresUtc = now.Add(MaximumReviewAge);
            var proposalSha256 = ComputeProposalSha256(
                canonicalPath,
                packageUri,
                Path.GetFileName(canonicalPath),
                lease.Length,
                packageSha256,
                evidence,
                now,
                expiresUtc);
            var proposal = new PackageInstallProposal(
                CurrentSchemaVersion,
                CurrentUserMsixOperation,
                DeploymentApiName,
                NoDeploymentOptions,
                CurrentUserScope,
                RequestsElevation: false,
                canonicalPath,
                packageUri,
                Path.GetFileName(canonicalPath),
                lease.Length,
                packageSha256,
                evidence.PackageIdentityName,
                evidence.PublisherId,
                evidence.Version,
                now,
                expiresUtc,
                proposalSha256);
            var session = new PackageInstallReviewSession(proposal, evidence, trustVerifier, lease);
            lease = null;
            return session;
        }
        catch (PackageInstallReviewException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
        {
            throw new PackageInstallReviewException("package_review_file_unavailable");
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static async Task<string> ComputeSha256Async(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexStringLower(digest);
    }

    internal static async Task<PackageInstallTrustEvidence> VerifyTrustAsync(
        IPackageInstallTrustVerifier trustVerifier,
        string packagePath,
        CancellationToken cancellationToken)
    {
        PackageInstallTrustEvidence? evidence;
        try
        {
            evidence = await trustVerifier.VerifyAsync(packagePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        ValidateTrustEvidence(evidence);
        return evidence;
    }

    internal static void ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new PackageInstallReviewException("package_review_time_invalid");
        }
    }

    private static string ValidatePackagePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new PackageInstallReviewException("package_review_path_invalid");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(packagePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PackageInstallReviewException("package_review_path_invalid");
        }

        if (!Path.IsPathFullyQualified(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".msix", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new PackageInstallReviewException("package_review_path_invalid");
        }

        if (OperatingSystem.IsWindows() &&
            (fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
             fullPath.AsSpan(Math.Min(2, fullPath.Length)).Contains(':')))
        {
            throw new PackageInstallReviewException("package_review_path_invalid");
        }

        RejectReparseHierarchy(fullPath);
        return fullPath;
    }

    private static void RejectReparseHierarchy(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new PackageInstallReviewException("package_review_path_invalid");
        }

        for (var current = file.Directory; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PackageInstallReviewException("package_review_path_invalid");
            }
        }
    }

    private static void ValidateTrustEvidence(PackageInstallTrustEvidence? evidence)
    {
        if (evidence is null || !evidence.SignatureValid ||
            !IsIdentifier(evidence.PackageIdentityName, 128) ||
            !IsPublisher(evidence.PublisherId))
        {
            throw new PackageInstallReviewException("package_review_trust_invalid");
        }

        try
        {
            _ = Reliability.ControlledUpdateManifestCodec.ParsePackageVersion(evidence.Version);
        }
        catch (Reliability.ControlledUpdateException)
        {
            throw new PackageInstallReviewException("package_review_trust_invalid");
        }
    }

    private static string ComputeProposalSha256(
        string packagePath,
        string packageUri,
        string packageFileName,
        long packageLength,
        string packageSha256,
        PackageInstallTrustEvidence evidence,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("operationKind", CurrentUserMsixOperation);
            writer.WriteString("deploymentApi", DeploymentApiName);
            writer.WriteString("deploymentOptions", NoDeploymentOptions);
            writer.WriteString("scope", CurrentUserScope);
            writer.WriteBoolean("requestsElevation", false);
            writer.WriteString("packagePath", packagePath);
            writer.WriteString("packageUri", packageUri);
            writer.WriteString("packageFileName", packageFileName);
            writer.WriteNumber("packageLength", packageLength);
            writer.WriteString("packageSha256", packageSha256);
            writer.WriteString("packageIdentityName", evidence.PackageIdentityName);
            writer.WriteString("publisherId", evidence.PublisherId);
            writer.WriteString("version", evidence.Version);
            writer.WriteString("createdUtc", createdUtc.ToString("O"));
            writer.WriteString("expiresUtc", expiresUtc.ToString("O"));
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsPublisher(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        value == value.Trim() && !value.Any(char.IsControl);
}

public sealed class PackageInstallReviewSession : IAsyncDisposable
{
    private readonly PackageInstallTrustEvidence initialTrustEvidence;
    private readonly IPackageInstallTrustVerifier trustVerifier;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? packageLease;

    internal PackageInstallReviewSession(
        PackageInstallProposal proposal,
        PackageInstallTrustEvidence initialTrustEvidence,
        IPackageInstallTrustVerifier trustVerifier,
        FileStream packageLease)
    {
        Proposal = proposal;
        this.initialTrustEvidence = initialTrustEvidence;
        this.trustVerifier = trustVerifier;
        this.packageLease = packageLease;
    }

    public PackageInstallProposal Proposal { get; }

    public PackageInstallReviewState State { get; private set; } = PackageInstallReviewState.Pending;

    public async Task<PackageInstallApprovalReceipt> ApproveForValidationAsync(
        bool explicitApproval,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequirePending();
            if (!explicitApproval)
            {
                throw new PackageInstallReviewException("package_review_approval_required");
            }

            PackageInstallReviewService.ValidateUtc(now);
            if (now < Proposal.CreatedUtc || now > Proposal.ExpiresUtc)
            {
                DenyAndRelease();
                throw new PackageInstallReviewException("package_review_expired");
            }

            try
            {
                var lease = packageLease ??
                    throw new PackageInstallReviewException("package_review_state_invalid");
                if (lease.Length != Proposal.PackageLength ||
                    !string.Equals(
                        await PackageInstallReviewService.ComputeSha256Async(lease, cancellationToken)
                            .ConfigureAwait(false),
                        Proposal.PackageSha256,
                        StringComparison.Ordinal))
                {
                    throw new PackageInstallReviewException("package_review_package_changed");
                }

                var currentTrust = await PackageInstallReviewService.VerifyTrustAsync(
                        trustVerifier,
                        Proposal.PackagePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (currentTrust != initialTrustEvidence)
                {
                    throw new PackageInstallReviewException("package_review_package_changed");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PackageInstallReviewException)
            {
                DenyAndRelease();
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                DenyAndRelease();
                throw new PackageInstallReviewException("package_review_file_unavailable");
            }

            State = PackageInstallReviewState.ApprovedForValidation;
            ReleaseLease();
            return new PackageInstallApprovalReceipt(
                Proposal.ProposalSha256,
                now,
                Proposal.OperationKind,
                ExecutionPermitted: false,
                InstallerProcessesStarted: 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequirePending();
            State = PackageInstallReviewState.Cancelled;
            ReleaseLease();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == PackageInstallReviewState.Pending)
            {
                State = PackageInstallReviewState.Disposed;
            }

            ReleaseLease();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private void RequirePending()
    {
        if (State != PackageInstallReviewState.Pending)
        {
            throw new PackageInstallReviewException("package_review_state_invalid");
        }
    }

    private void DenyAndRelease()
    {
        State = PackageInstallReviewState.Denied;
        ReleaseLease();
    }

    private void ReleaseLease()
    {
        packageLease?.Dispose();
        packageLease = null;
    }
}
