using System.Security.Cryptography;

namespace DCx.Core.Knowledge;

public interface IKnowledgeArtifactValidator
{
    void Validate(ReadOnlyMemory<byte> content);
}

public sealed class ContentAddressedArtifactStore
{
    public const int MaximumArtifactBytes = 32 * 1024 * 1024;

    private readonly string root;

    public ContentAddressedArtifactStore(string privateStoreRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateStoreRoot);
        root = Path.GetFullPath(Path.Combine(privateStoreRoot, "artifacts", "sha256"));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new KnowledgeArtifactException("The private artifact root cannot be a reparse point.");
        }
    }

    public async Task<string> PutAsync(
        ReadOnlyMemory<byte> content,
        string expectedSha256,
        IKnowledgeArtifactValidator? validator = null,
        CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty || content.Length > MaximumArtifactBytes)
        {
            throw new KnowledgeArtifactException("The artifact is empty or exceeds the private-store limit.");
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        if (!IsSha256(expectedSha256) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(expectedSha256)))
        {
            throw new KnowledgeArtifactException("The artifact digest does not match its declared SHA-256.");
        }

        validator?.Validate(content);
        var directory = Path.Combine(root, digest[..2]);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, digest);
        if (File.Exists(destination))
        {
            return digest;
        }

        var temporary = Path.Combine(directory, $".{digest}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }

            return digest;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public string ResolveExisting(string sha256)
    {
        if (!IsSha256(sha256))
        {
            throw new KnowledgeArtifactException("The artifact SHA-256 is malformed.");
        }

        var normalized = sha256.ToLowerInvariant();
        var path = Path.Combine(root, normalized[..2], normalized);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The private artifact is not present.");
        }

        var information = new FileInfo(path);
        if (information.Length is <= 0 or > MaximumArtifactBytes)
        {
            throw new KnowledgeArtifactException("The stored private artifact has an invalid size.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualDigest = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, Convert.FromHexString(normalized)))
        {
            throw new KnowledgeArtifactException("The stored private artifact failed its SHA-256 integrity check.");
        }

        return path;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));
}
