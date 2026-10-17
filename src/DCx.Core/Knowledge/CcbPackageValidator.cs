using System.IO.Compression;
using System.Text.Json;

namespace DCx.Core.Knowledge;

public sealed record CcbPackageManifest(string Id, string Version, string Entrypoint);

public sealed class CcbPackageValidator : IKnowledgeArtifactValidator
{
    public const int MaximumEntries = 256;
    public const long MaximumExpandedBytes = 64L * 1024 * 1024;
    public const int MaximumManifestBytes = 128 * 1024;

    public void Validate(ReadOnlyMemory<byte> content)
    {
        _ = ReadManifest(content);
    }

    public CcbPackageManifest ReadManifest(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is 0 or > MaximumEntries)
            {
                throw new KnowledgeArtifactException("The CCB package has an invalid number of entries.");
            }

            long expandedBytes = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            foreach (var entry in archive.Entries)
            {
                ValidatePath(entry.FullName);
                if (!paths.Add(entry.FullName))
                {
                    throw new KnowledgeArtifactException("The CCB package contains duplicate paths.");
                }

                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                {
                    throw new KnowledgeArtifactException("The CCB package cannot contain symbolic links.");
                }

                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumExpandedBytes ||
                    (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 1000))
                {
                    throw new KnowledgeArtifactException("The CCB package exceeds expansion limits.");
                }

                if (entry.FullName.Equals("dcx-tool.json", StringComparison.Ordinal))
                {
                    manifestEntry = entry;
                }
            }

            if (manifestEntry is null || manifestEntry.Length is 0 or > MaximumManifestBytes)
            {
                throw new KnowledgeArtifactException("The CCB package requires a bounded root dcx-tool.json manifest.");
            }

            using var manifestStream = manifestEntry.Open();
            using var document = JsonDocument.Parse(manifestStream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !TryBoundedString(root, "id", 128, out var id) ||
                !TryBoundedString(root, "version", 64, out var version) ||
                !TryBoundedString(root, "entrypoint", 256, out var entrypoint))
            {
                throw new KnowledgeArtifactException("The CCB package manifest is malformed or unsupported.");
            }

            ValidatePath(entrypoint!);
            if (!paths.Contains(entrypoint!))
            {
                throw new KnowledgeArtifactException("The CCB package entrypoint does not exist.");
            }

            return new CcbPackageManifest(id!, version!, entrypoint!);
        }
        catch (KnowledgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or OverflowException or InvalidOperationException or FormatException)
        {
            throw new KnowledgeArtifactException($"The CCB package is malformed: {exception.GetType().Name}.");
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\0') ||
            path.Contains('\\') || path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new KnowledgeArtifactException("The CCB package contains an unsafe path.");
        }
    }

    private static bool TryBoundedString(JsonElement element, string property, int maximumLength, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
    }
}
