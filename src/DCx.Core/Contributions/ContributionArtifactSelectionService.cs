using System.Security;
using System.Security.Cryptography;
using DCx.Core.Knowledge;

namespace DCx.Core.Contributions;

public sealed class ContributionArtifactException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class SelectedContributionArtifact : IDisposable
{
    private byte[]? content;

    internal SelectedContributionArtifact(
        string fileName,
        byte[] content,
        string sha256,
        CcbContributionPackage package)
    {
        FileName = fileName;
        this.content = content;
        Sha256 = sha256;
        Manifest = package.Manifest;
        ReviewText = package.ReviewText;
        LicenseText = package.LicenseText;
        DeclaredLicenses = package.DeclaredLicenses.ToArray();
    }

    public string FileName { get; }
    public int SizeBytes => content?.Length ?? 0;
    public string Sha256 { get; }
    public CcbPackageManifest Manifest { get; }
    public string ReviewText { get; }
    public string LicenseText { get; }
    public IReadOnlyList<string> DeclaredLicenses { get; }

    public ReadOnlyMemory<byte> Content => content is { } value
        ? value
        : throw new ObjectDisposedException(nameof(SelectedContributionArtifact));

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref content, null);
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
}

public sealed class ContributionArtifactSelectionService
{
    private readonly CcbPackageValidator validator = new();

    public async Task<SelectedContributionArtifact> LoadCcbAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ContributionArtifactException("ccb_artifact_path_required");
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContributionArtifactException("ccb_artifact_path_invalid");
        }

        byte[]? content = null;
        try
        {
            var file = new FileInfo(canonical);
            file.Refresh();
            ValidateFile(file);
            await using var stream = new FileStream(
                canonical,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.Refresh();
            ValidateFile(file);
            if (stream.Length != file.Length)
            {
                throw new ContributionArtifactException("ccb_artifact_changed");
            }

            content = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            var offset = 0;
            while (offset < content.Length)
            {
                var read = await stream.ReadAsync(
                    content.AsMemory(offset, content.Length - offset),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new ContributionArtifactException("ccb_artifact_changed");
                offset += read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new ContributionArtifactException("ccb_artifact_changed");
            }

            CcbContributionPackage package;
            try
            {
                package = validator.ReadContributionPackage(content);
            }
            catch (KnowledgeArtifactException)
            {
                throw new ContributionArtifactException("ccb_artifact_policy_invalid");
            }

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            var fileName = SafeFileName(file.Name);
            var selection = new SelectedContributionArtifact(fileName, content, sha256, package);
            content = null;
            return selection;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ContributionArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new ContributionArtifactException("ccb_artifact_read_failed");
        }
        finally
        {
            if (content is not null) CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ValidateFile(FileInfo file)
    {
        if (!file.Exists || file.Length is <= 0 or > ContentAddressedArtifactStore.MaximumArtifactBytes ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new ContributionArtifactException("ccb_artifact_unsafe");
        }
    }

    private static string SafeFileName(string value) =>
        value.Length is > 0 and <= 256 && value.All(character => !char.IsControl(character))
            ? value
            : "selected-package.zip";
}
