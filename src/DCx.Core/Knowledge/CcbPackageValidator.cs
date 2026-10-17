using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCx.Core.Knowledge;

public sealed record CcbPackageManifest(
    string Id,
    string Version,
    string Entrypoint,
    int SchemaVersion = 1,
    string? Runtime = null,
    string? TestsPath = null,
    string? SbomPath = null,
    string? LicensePath = null);

public sealed record CcbContributionTestCase(string Name, byte[] Input, string ExpectedOutputSha256);

public sealed record CcbContributionPackage(
    CcbPackageManifest Manifest,
    IReadOnlyList<CcbContributionTestCase> Tests,
    string TestSuiteSha256,
    string SbomSha256,
    string LicenseSha256,
    string LicenseText,
    string EntrypointText,
    string ReviewText,
    IReadOnlyList<string> DeclaredLicenses);

public sealed class CcbPackageValidator : IKnowledgeArtifactValidator
{
    public const int MaximumEntries = 256;
    public const long MaximumExpandedBytes = 64L * 1024 * 1024;
    public const int MaximumManifestBytes = 128 * 1024;
    public const int MaximumContributionMetadataBytes = 1024 * 1024;
    public const int MaximumContributionTests = 32;

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

            foreach (var path in paths)
            {
                var segments = path.Split('/');
                for (var count = 1; count < segments.Length; count++)
                {
                    if (paths.Contains(string.Join('/', segments.Take(count))))
                    {
                        throw new KnowledgeArtifactException("The CCB package contains a file/directory path collision.");
                    }
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
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion is not 1 and not 2 ||
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

            if (schemaVersion == 1)
            {
                return new CcbPackageManifest(id!, version!, entrypoint!);
            }

            if (!entrypoint!.EndsWith(".sh", StringComparison.Ordinal) ||
                !TryBoundedString(root, "runtime", 64, out var runtime) || runtime != "busybox-sh-v1" ||
                !TryBoundedString(root, "tests", 256, out var testsPath) ||
                !TryBoundedString(root, "sbom", 256, out var sbomPath) ||
                !TryBoundedString(root, "licenseFile", 256, out var licensePath) ||
                !HasExactProperties(root,
                    "schemaVersion", "id", "version", "entrypoint", "runtime", "tests", "sbom", "licenseFile"))
            {
                throw new KnowledgeArtifactException("The contribution CCB manifest is incomplete or unsupported.");
            }

            foreach (var requiredPath in new[] { testsPath!, sbomPath!, licensePath! })
            {
                ValidatePath(requiredPath);
                if (!paths.Contains(requiredPath))
                {
                    throw new KnowledgeArtifactException("The contribution CCB metadata file is missing.");
                }
            }

            var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dcx-tool.json",
                entrypoint,
                testsPath!,
                sbomPath!,
                licensePath!,
            };
            if (allowedPaths.Count != 5 || !paths.SetEquals(allowedPaths))
            {
                throw new KnowledgeArtifactException("The contribution CCB package contains undeclared files or path aliases.");
            }

            return new CcbPackageManifest(
                id!, version!, entrypoint, schemaVersion, runtime, testsPath, sbomPath, licensePath);
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

    public CcbContributionPackage ReadContributionPackage(ReadOnlyMemory<byte> content)
    {
        var manifest = ReadManifest(content);
        if (manifest.SchemaVersion != 2 || manifest.TestsPath is null || manifest.SbomPath is null ||
            manifest.LicensePath is null || manifest.Runtime != "busybox-sh-v1")
        {
            throw new KnowledgeArtifactException("The CCB package is not an authoritative contribution package.");
        }

        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestBytes = ReadBoundedEntry(archive, "dcx-tool.json", MaximumManifestBytes);
        var testsBytes = ReadBoundedEntry(archive, manifest.TestsPath, MaximumContributionMetadataBytes);
        var sbomBytes = ReadBoundedEntry(archive, manifest.SbomPath, MaximumContributionMetadataBytes);
        var licenseBytes = ReadBoundedEntry(archive, manifest.LicensePath, MaximumContributionMetadataBytes);
        var entrypointBytes = ReadBoundedEntry(archive, manifest.Entrypoint, 64 * 1024);
        var tests = ParseTests(testsBytes);
        var declaredLicenses = ValidateSbom(sbomBytes, manifest);
        var licenseText = DecodeUtf8(licenseBytes, "license");
        var entrypointText = DecodeUtf8(entrypointBytes, "entrypoint");
        var reviewText = string.Join('\n',
            entrypointText,
            licenseText,
            DecodeUtf8(manifestBytes, "manifest"),
            DecodeUtf8(testsBytes, "test suite"),
            DecodeUtf8(sbomBytes, "SBOM"),
            string.Join('\n', tests.Select(test => DecodeUtf8(test.Input, $"test input {test.Name}"))));
        if (string.IsNullOrWhiteSpace(licenseText) || licenseText.Contains('\0') ||
            string.IsNullOrWhiteSpace(entrypointText) || entrypointText.Contains('\0'))
        {
            throw new KnowledgeArtifactException("The contribution CCB license or entrypoint is empty or invalid.");
        }

        return new CcbContributionPackage(
            manifest,
            tests,
            Sha256(testsBytes),
            Sha256(sbomBytes),
            Sha256(licenseBytes),
            licenseText,
            entrypointText,
            reviewText,
            declaredLicenses);
    }

    public CcbPackageManifest ExtractValidated(ReadOnlyMemory<byte> content, string destination)
    {
        var manifest = ReadManifest(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new KnowledgeArtifactException("The CCB extraction destination must be empty.");
        }

        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new KnowledgeArtifactException("The CCB extraction destination cannot be a link.");
        }

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                ValidatePath(entry.FullName);
                var destinationPath = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(root, destinationPath);
                if (Path.IsPathRooted(relative) || relative is "." or ".." ||
                    relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new KnowledgeArtifactException("The CCB extraction path escaped its destination.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var input = entry.Open();
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                if (output.Length != entry.Length)
                {
                    throw new KnowledgeArtifactException("The CCB extracted entry length is inconsistent.");
                }
            }

            return manifest;
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
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

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Length &&
               properties.Distinct(StringComparer.Ordinal).Count() == properties.Length &&
               properties.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool HasUniqueObjectProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueObjectProperties(property.Value)) return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasUniqueObjectProperties(item)) return false;
            }
        }

        return true;
    }

    private static byte[] ReadBoundedEntry(ZipArchive archive, string path, int maximumBytes)
    {
        var entry = archive.GetEntry(path)
            ?? throw new KnowledgeArtifactException("The contribution CCB metadata file is missing.");
        if (entry.Length is <= 0 || entry.Length > maximumBytes)
        {
            throw new KnowledgeArtifactException("A contribution CCB metadata file exceeds its limit.");
        }

        using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        input.CopyTo(output);
        if (output.Length != entry.Length)
        {
            throw new KnowledgeArtifactException("A contribution CCB metadata file has an inconsistent length.");
        }

        return output.ToArray();
    }

    private static IReadOnlyList<CcbContributionTestCase> ParseTests(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasUniqueObjectProperties(root) ||
                !HasExactProperties(root, "schemaVersion", "tests") ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !root.TryGetProperty("tests", out var testsElement) || testsElement.ValueKind != JsonValueKind.Array ||
                testsElement.GetArrayLength() is <= 0 or > MaximumContributionTests)
            {
                throw new KnowledgeArtifactException("The contribution CCB test suite is malformed.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var tests = new List<CcbContributionTestCase>();
            long totalInputBytes = 0;
            foreach (var test in testsElement.EnumerateArray())
            {
                if (test.ValueKind != JsonValueKind.Object ||
                    !HasExactProperties(test, "name", "inputBase64", "expectedOutputSha256") ||
                    !TryBoundedString(test, "name", 128, out var name) || !names.Add(name!) ||
                    !TryBoundedString(test, "inputBase64", 2 * CcbSandboxInputLimit, out var inputBase64) ||
                    !TryBoundedString(test, "expectedOutputSha256", 64, out var expectedOutputSha256) ||
                    expectedOutputSha256!.Length != 64 || !expectedOutputSha256.All(char.IsAsciiHexDigit))
                {
                    throw new KnowledgeArtifactException("A contribution CCB test case is malformed.");
                }

                byte[] input;
                try
                {
                    input = Convert.FromBase64String(inputBase64!);
                }
                catch (FormatException)
                {
                    throw new KnowledgeArtifactException("A contribution CCB test input is not valid base64.");
                }

                totalInputBytes = checked(totalInputBytes + input.Length);
                if (input.Length > CcbSandboxInputLimit || totalInputBytes > 4L * CcbSandboxInputLimit)
                {
                    throw new KnowledgeArtifactException("The contribution CCB test input exceeds its limit.");
                }

                var inputText = DecodeUtf8(input, "test input");
                if (inputText.Contains('\0'))
                {
                    throw new KnowledgeArtifactException("A contribution CCB test input contains a null character.");
                }

                tests.Add(new CcbContributionTestCase(name!, input, expectedOutputSha256.ToLowerInvariant()));
            }

            return tests.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
        catch (KnowledgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new KnowledgeArtifactException($"The contribution CCB test suite is malformed: {exception.GetType().Name}.");
        }
    }

    private static IReadOnlyList<string> ValidateSbom(byte[] content, CcbPackageManifest manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasUniqueObjectProperties(root) ||
                !root.TryGetProperty("bomFormat", out var format) || format.GetString() != "CycloneDX" ||
                !TryBoundedString(root, "specVersion", 16, out _) ||
                !root.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array ||
                components.GetArrayLength() is <= 0 or > 256)
            {
                throw new KnowledgeArtifactException("The contribution CCB SBOM is malformed or unsupported.");
            }

            var packageFound = false;
            var runtimeFound = false;
            var packageLicenses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object ||
                    !TryBoundedString(component, "name", 256, out var name) ||
                    !TryBoundedString(component, "version", 128, out var version))
                {
                    throw new KnowledgeArtifactException("A contribution CCB SBOM component is malformed.");
                }

                var isPackage = name == manifest.Id && version == manifest.Version;
                packageFound |= isPackage;
                runtimeFound |= name == "busybox-sh-v1" && version == "pinned-by-sandbox";
                if (!isPackage) continue;
                if (!component.TryGetProperty("licenses", out var licenses) ||
                    licenses.ValueKind != JsonValueKind.Array || licenses.GetArrayLength() is <= 0 or > 8)
                {
                    throw new KnowledgeArtifactException("The contribution CCB SBOM package license is missing.");
                }

                foreach (var license in licenses.EnumerateArray())
                {
                    if (license.ValueKind != JsonValueKind.Object ||
                        !license.TryGetProperty("license", out var licenseNode) ||
                        licenseNode.ValueKind != JsonValueKind.Object ||
                        !TryBoundedString(licenseNode, "id", 64, out var id))
                    {
                        throw new KnowledgeArtifactException("The contribution CCB SBOM package license is malformed.");
                    }

                    packageLicenses.Add(id!);
                }
            }

            if (!packageFound || !runtimeFound || components.GetArrayLength() != 2 || packageLicenses.Count == 0)
            {
                throw new KnowledgeArtifactException("The contribution CCB SBOM does not close the package runtime dependency set.");
            }

            return packageLicenses.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        catch (KnowledgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new KnowledgeArtifactException($"The contribution CCB SBOM is malformed: {exception.GetType().Name}.");
        }
    }

    private static string DecodeUtf8(byte[] content, string label)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new KnowledgeArtifactException($"The contribution CCB {label} is not valid UTF-8.");
        }
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private const int CcbSandboxInputLimit = 1024 * 1024;
}
