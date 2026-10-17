using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Contributions;
using DCx.Core.Knowledge;

namespace DCx.Core.Tests.Contributions;

public sealed class CcbContributionPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-ccb-package-{Guid.NewGuid():N}");

    [Fact]
    public async Task SchemaTwoPackage_ValidatesExtractsAndProducesCanonicalWorkspaceDigest()
    {
        var packageBytes = CreatePackage();
        var validator = new CcbPackageValidator();

        var package = validator.ReadContributionPackage(packageBytes);
        var extraction = Path.Combine(root, "extracted");
        var extractedManifest = validator.ExtractValidated(packageBytes, extraction);
        var workspaceSha256 = await CcbSandboxEvidence.ComputeWorkspaceSha256Async(extraction);

        Assert.Equal(2, package.Manifest.SchemaVersion);
        Assert.Equal("busybox-sh-v1", package.Manifest.Runtime);
        Assert.Equal("tool:community-upper", extractedManifest.Id);
        Assert.Equal(2, package.Tests.Count);
        Assert.All(package.Tests, test => Assert.Equal(64, test.ExpectedOutputSha256.Length));
        Assert.Equal(64, package.TestSuiteSha256.Length);
        Assert.Equal(64, package.SbomSha256.Length);
        Assert.Contains("MIT", package.LicenseText, StringComparison.Ordinal);
        Assert.Equal(64, workspaceSha256.Length);
        Assert.True(File.Exists(Path.Combine(extraction, "tool.sh")));
    }

    [Fact]
    public void SchemaTwoPackage_RejectsMissingMetadataInvalidTestsAndMismatchedSbom()
    {
        var validator = new CcbPackageValidator();

        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(includeLicense: false)));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(testInputBase64: "not base64")));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(testInputBase64: "/w==")));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(testInputBase64: "AA==")));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(sbomId: "different-package")));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(includeExtraFile: true)));
        Assert.Throws<KnowledgeArtifactException>(() =>
            validator.ReadContributionPackage(CreatePackage(includeUnknownManifestProperty: true)));
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    internal static byte[] CreatePackage(
        bool includeLicense = true,
        string testInputBase64 = "YWJj",
        string sbomId = "tool:community-upper",
        string licenseId = "MIT",
        string licenseText = "MIT License\nPermission is hereby granted.\n",
        bool includeExtraFile = false,
        bool includeUnknownManifestProperty = false,
        string entrypointText = "/runtime/busybox tr 'abcdefghijklmnopqrstuvwxyz' 'ABCDEFGHIJKLMNOPQRSTUVWXYZ' < /input/request\n")
    {
        var firstOutput = Digest("ABC");
        var secondOutput = Digest("DCX");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "dcx-tool.json",
                includeUnknownManifestProperty
                    ? """
                      {"schemaVersion":2,"id":"tool:community-upper","version":"1.0.0","entrypoint":"tool.sh","runtime":"busybox-sh-v1","tests":"dcx-tests.json","sbom":"sbom.cdx.json","licenseFile":"LICENSE","note":"undeclared"}
                      """
                    : """
                      {"schemaVersion":2,"id":"tool:community-upper","version":"1.0.0","entrypoint":"tool.sh","runtime":"busybox-sh-v1","tests":"dcx-tests.json","sbom":"sbom.cdx.json","licenseFile":"LICENSE"}
                      """);
            Write(archive, "tool.sh", entrypointText);
            Write(archive, "dcx-tests.json",
                $$"""
                {"schemaVersion":1,"tests":[{"name":"ascii","inputBase64":"{{testInputBase64}}","expectedOutputSha256":"{{firstOutput}}"},{"name":"dcx","inputBase64":"REN4","expectedOutputSha256":"{{secondOutput}}"}]}
                """);
            Write(archive, "sbom.cdx.json",
                $$$"""
                {"bomFormat":"CycloneDX","specVersion":"1.6","components":[{"type":"application","name":"{{{sbomId}}}","version":"1.0.0","licenses":[{"license":{"id":"{{{licenseId}}}"}}]},{"type":"framework","name":"busybox-sh-v1","version":"pinned-by-sandbox"}]}
                """);
            if (includeLicense) Write(archive, "LICENSE", licenseText);
            if (includeExtraFile) Write(archive, "hidden.txt", "undeclared");
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
