using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Security;

namespace DCx.Core.GlobalKnowledge;

public sealed record GlobalRegistryEnrollment(
    int SchemaVersion,
    string RegistryBaseUri,
    string TrustRootSubjectPublicKeyInfoBase64,
    string TrustRootSha256,
    DateTimeOffset ApprovedUtc);

public sealed class GlobalRegistryEnrollmentReview : IDisposable
{
    private byte[] trustRoot;

    internal GlobalRegistryEnrollmentReview(
        byte[] trustRoot,
        string fileName,
        Uri registryBaseUri,
        string trustRootSha256,
        DateTimeOffset reviewedUtc)
    {
        this.trustRoot = trustRoot;
        FileName = fileName;
        RegistryBaseUri = registryBaseUri;
        TrustRootSha256 = trustRootSha256;
        ReviewedUtc = reviewedUtc;
    }

    public string FileName { get; }

    public int TrustRootBytes => trustRoot.Length;

    public Uri RegistryBaseUri { get; }

    public string TrustRootSha256 { get; }

    public DateTimeOffset ReviewedUtc { get; }

    internal byte[] CopyTrustRoot()
    {
        if (trustRoot.Length == 0)
        {
            throw new ObjectDisposedException(nameof(GlobalRegistryEnrollmentReview));
        }

        return trustRoot.ToArray();
    }

    public void Dispose()
    {
        if (trustRoot.Length == 0) return;
        CryptographicOperations.ZeroMemory(trustRoot);
        trustRoot = [];
    }
}

public sealed class GlobalRegistryEnrollmentReviewer
{
    public const int MinimumTrustRootBytes = 64;
    public const int MaximumTrustRootBytes = 1024;

    public async Task<GlobalRegistryEnrollmentReview> ReviewAsync(
        string trustRootPath,
        string registryBaseUri,
        DateTimeOffset reviewedUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trustRootPath) || trustRootPath.Length > 2048 ||
            string.IsNullOrWhiteSpace(registryBaseUri) ||
            !Uri.TryCreate(registryBaseUri.Trim(), UriKind.Absolute, out var parsedEndpoint))
        {
            throw new GlobalReplicaException("The global registry enrollment input is invalid.");
        }

        var endpoint = GlobalRegistryEndpointPolicy.Normalize(parsedEndpoint);
        var path = GlobalStoragePathGuard.ValidateFile(
            trustRootPath.Trim(),
            mustExist: true,
            "global trust-root candidate");
        byte[] bytes = [];
        try
        {
            await using (var stream = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (stream.Length is < MinimumTrustRootBytes or > MaximumTrustRootBytes)
                {
                    throw new GlobalReplicaException(
                        "The global trust-root candidate is outside its size limit.");
                }

                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (stream.Position != stream.Length)
                {
                    throw new GlobalReplicaException(
                        "The global trust-root candidate changed during review.");
                }
            }

            GlobalRegistryEnrollmentPolicy.ValidateTrustRoot(bytes);
            return new GlobalRegistryEnrollmentReview(
                bytes,
                Path.GetFileName(path),
                endpoint,
                GlobalReleaseCodec.Sha256(bytes),
                reviewedUtc.ToUniversalTime());
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }
}

public interface IGlobalRegistryEnrollmentStore
{
    Task<GlobalRegistryEnrollment?> ReadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        GlobalRegistryEnrollmentReview review,
        bool explicitlyApproved,
        DateTimeOffset approvedUtc,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class WindowsGlobalRegistryEnrollmentStore : IGlobalRegistryEnrollmentStore
{
    public const int CurrentSchemaVersion = 1;
    internal const string DefaultTargetName = "DCx/GlobalRegistry/Enrollment/v1";
    private const int MaximumCredentialCharacters = 4 * 1024;
    private static readonly object VaultLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 4,
    };

    private readonly WindowsCredentialVault vault;
    private readonly string targetName;

    public WindowsGlobalRegistryEnrollmentStore()
        : this(new WindowsCredentialVault(), DefaultTargetName)
    {
    }

    internal WindowsGlobalRegistryEnrollmentStore(
        WindowsCredentialVault vault,
        string targetName)
    {
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.targetName = !string.IsNullOrWhiteSpace(targetName) && targetName.Length <= 256
            ? targetName
            : throw new ArgumentException("The enrollment credential target is invalid.", nameof(targetName));
    }

    public Task<GlobalRegistryEnrollment?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (VaultLock)
        {
            string? content;
            try
            {
                content = vault.Read(targetName);
            }
            catch (Win32Exception exception)
            {
                throw new GlobalReplicaException(
                    $"The protected global registry enrollment could not be read: {exception.GetType().Name}.");
            }

            if (content is null) return Task.FromResult<GlobalRegistryEnrollment?>(null);
            return Task.FromResult<GlobalRegistryEnrollment?>(Decode(content));
        }
    }

    public Task SaveAsync(
        GlobalRegistryEnrollmentReview review,
        bool explicitlyApproved,
        DateTimeOffset approvedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        cancellationToken.ThrowIfCancellationRequested();
        if (!explicitlyApproved)
        {
            throw new GlobalReplicaException("Global registry enrollment requires explicit approval.");
        }

        var approved = approvedUtc.ToUniversalTime();
        if (review.ReviewedUtc == default || review.ReviewedUtc.Offset != TimeSpan.Zero ||
            approved == default || approved.Offset != TimeSpan.Zero || approved < review.ReviewedUtc ||
            approved - review.ReviewedUtc > TimeSpan.FromMinutes(30))
        {
            throw new GlobalReplicaException("The global registry enrollment review is stale or invalid.");
        }

        var trustRoot = review.CopyTrustRoot();
        try
        {
            var enrollment = new GlobalRegistryEnrollment(
                CurrentSchemaVersion,
                review.RegistryBaseUri.AbsoluteUri,
                Convert.ToBase64String(trustRoot),
                review.TrustRootSha256,
                approved);
            GlobalRegistryEnrollmentPolicy.Validate(enrollment);
            var content = JsonSerializer.Serialize(enrollment, JsonOptions);
            if (content.Length is <= 0 or > MaximumCredentialCharacters || content.Contains('\0'))
            {
                throw new GlobalReplicaException("The protected global registry enrollment exceeds its limit.");
            }

            lock (VaultLock)
            {
                try
                {
                    vault.Save(targetName, content);
                }
                catch (Win32Exception exception)
                {
                    throw new GlobalReplicaException(
                        $"The protected global registry enrollment could not be written: {exception.GetType().Name}.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustRoot);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (VaultLock)
        {
            try
            {
                _ = vault.Delete(targetName);
            }
            catch (Win32Exception exception)
            {
                throw new GlobalReplicaException(
                    $"The protected global registry enrollment could not be cleared: {exception.GetType().Name}.");
            }
        }

        return Task.CompletedTask;
    }

    private static GlobalRegistryEnrollment Decode(string content)
    {
        if (content.Length is <= 0 or > MaximumCredentialCharacters || content.Contains('\0'))
        {
            throw new GlobalReplicaException("The protected global registry enrollment is outside its limit.");
        }

        try
        {
            var enrollment = JsonSerializer.Deserialize<GlobalRegistryEnrollment>(content, JsonOptions)
                ?? throw new GlobalReplicaException("The protected global registry enrollment is empty.");
            GlobalRegistryEnrollmentPolicy.Validate(enrollment);
            return enrollment;
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException)
        {
            throw new GlobalReplicaException(
                $"The protected global registry enrollment is malformed: {exception.GetType().Name}.");
        }
    }
}

public static class GlobalRegistryEnrollmentPolicy
{
    public static void Validate(GlobalRegistryEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (enrollment.SchemaVersion != WindowsGlobalRegistryEnrollmentStore.CurrentSchemaVersion ||
            enrollment.ApprovedUtc == default || enrollment.ApprovedUtc.Offset != TimeSpan.Zero ||
            !Uri.TryCreate(enrollment.RegistryBaseUri, UriKind.Absolute, out var parsedEndpoint) ||
            !string.Equals(
                GlobalRegistryEndpointPolicy.Normalize(parsedEndpoint).AbsoluteUri,
                enrollment.RegistryBaseUri,
                StringComparison.Ordinal) ||
            enrollment.TrustRootSubjectPublicKeyInfoBase64 is not { Length: > 0 and <= 2048 } ||
            enrollment.TrustRootSha256 is not { Length: 64 } ||
            !enrollment.TrustRootSha256.All(char.IsAsciiHexDigit))
        {
            throw new GlobalReplicaException("The protected global registry enrollment fields are invalid.");
        }

        byte[] trustRoot;
        try
        {
            trustRoot = Convert.FromBase64String(enrollment.TrustRootSubjectPublicKeyInfoBase64);
        }
        catch (FormatException exception)
        {
            throw new GlobalReplicaException(
                $"The protected global registry trust root is malformed: {exception.GetType().Name}.");
        }
        try
        {
            ValidateTrustRoot(trustRoot);
            if (!string.Equals(
                    GlobalReleaseCodec.Sha256(trustRoot),
                    enrollment.TrustRootSha256,
                    StringComparison.Ordinal))
            {
                throw new GlobalReplicaException("The protected global registry trust-root digest conflicts.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustRoot);
        }
    }

    public static byte[] ExportTrustRoot(GlobalRegistryEnrollment enrollment)
    {
        Validate(enrollment);
        return Convert.FromBase64String(enrollment.TrustRootSubjectPublicKeyInfoBase64);
    }

    internal static void ValidateTrustRoot(ReadOnlySpan<byte> trustRoot)
    {
        if (trustRoot.Length is < GlobalRegistryEnrollmentReviewer.MinimumTrustRootBytes or
            > GlobalRegistryEnrollmentReviewer.MaximumTrustRootBytes)
        {
            throw new GlobalReplicaException("The global registry trust root has an invalid size.");
        }

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(trustRoot, out var bytesRead);
            if (bytesRead != trustRoot.Length || verifier.KeySize != 256)
            {
                throw new GlobalReplicaException("The global registry trust root must be an exact P-256 public key.");
            }
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new GlobalReplicaException(
                $"The global registry trust root is invalid: {exception.GetType().Name}.");
        }
    }
}
