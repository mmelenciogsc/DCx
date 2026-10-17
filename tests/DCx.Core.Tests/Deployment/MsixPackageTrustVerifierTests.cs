using System.IO.Compression;
using System.Text;
using DCx.Core.Deployment;

namespace DCx.Core.Tests.Deployment;

public sealed class MsixPackageTrustVerifierTests : IDisposable
{
    private const string Publisher = "CN=DCx Test Publisher";
    private static readonly string CertificateSha256 = new('b', 64);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-msix-trust-{Guid.NewGuid():N}");

    public MsixPackageTrustVerifierTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ManifestInspector_ReadsExactFoundationIdentity()
    {
        var package = CreatePackage(ValidManifest());

        var identity = await new MsixManifestInspector().InspectAsync(package);

        Assert.Equal("DCx.AgenticBrowser", identity.PackageIdentityName);
        Assert.Equal(Publisher, identity.PublisherId);
        Assert.Equal("1.2.3.4", identity.Version);
    }

    [Fact]
    public async Task Verifier_AcceptsMatchingValidSignatureAndBindsCertificate()
    {
        var package = CreatePackage(ValidManifest());
        var verifier = new MsixPackageTrustVerifier(new FixtureSignatureProbe(
            new PackageAuthenticodeEvidence("Valid", Publisher, CertificateSha256)));

        var evidence = await verifier.VerifyAsync(package, CancellationToken.None);

        Assert.True(evidence.SignatureValid);
        Assert.Equal("DCx.AgenticBrowser", evidence.PackageIdentityName);
        Assert.Equal(Publisher, evidence.PublisherId);
        Assert.Equal("1.2.3.4", evidence.Version);
        Assert.Equal(CertificateSha256, evidence.SignerCertificateSha256);
    }

    [Theory]
    [InlineData("NotSigned")]
    [InlineData("HashMismatch")]
    [InlineData("NotTrusted")]
    public async Task Verifier_DeniesNonvalidSignatureStatus(string status)
    {
        var package = CreatePackage(ValidManifest());
        var verifier = new MsixPackageTrustVerifier(new FixtureSignatureProbe(
            new PackageAuthenticodeEvidence(status, null, null)));

        var evidence = await verifier.VerifyAsync(package, CancellationToken.None);

        Assert.False(evidence.SignatureValid);
        Assert.Equal(new string('0', 64), evidence.SignerCertificateSha256);
    }

    [Fact]
    public async Task Verifier_DeniesSignerSubjectThatDoesNotExactlyMatchManifestPublisher()
    {
        var package = CreatePackage(ValidManifest());
        var verifier = new MsixPackageTrustVerifier(new FixtureSignatureProbe(
            new PackageAuthenticodeEvidence(
                "Valid",
                "CN=Different Publisher",
                CertificateSha256)));

        var evidence = await verifier.VerifyAsync(package, CancellationToken.None);

        Assert.False(evidence.SignatureValid);
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public async Task ManifestInspector_DeniesMalformedOrAmbiguousIdentity(string manifest)
    {
        var package = CreatePackage(manifest);

        await AssertCodeAsync(
            "package_review_msix_manifest_invalid",
            () => new MsixManifestInspector().InspectAsync(package));
    }

    [Fact]
    public async Task ManifestInspector_DeniesCaseVariantDuplicateManifest()
    {
        var package = CreatePackage(
            ValidManifest(),
            ("appxmanifest.xml", ValidManifest()));

        await AssertCodeAsync(
            "package_review_msix_manifest_invalid",
            () => new MsixManifestInspector().InspectAsync(package));
    }

    [Fact]
    public async Task ManifestInspector_DeniesOversizedManifestBeforeParsing()
    {
        var package = CreatePackage(
            ValidManifest() + new string(' ', checked((int)MsixManifestInspector.MaximumManifestBytes)));

        await AssertCodeAsync(
            "package_review_msix_manifest_invalid",
            () => new MsixManifestInspector().InspectAsync(package));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"status\":\"Unexpected\",\"signerSubject\":null,\"signerCertificateSha256\":null}")]
    [InlineData("{\"schemaVersion\":1,\"status\":\"Valid\",\"signerSubject\":null,\"signerCertificateSha256\":null}")]
    [InlineData("{\"schemaVersion\":1,\"status\":\"NotSigned\",\"signerSubject\":null,\"signerCertificateSha256\":null,\"extra\":true}")]
    public void AuthenticodeProbe_DeniesMalformedOrUnknownOutput(string json)
    {
        var exception = Assert.Throws<PackageInstallReviewException>(
            () => WindowsPowerShellAuthenticodeProbe.Decode(json));

        Assert.Equal("package_review_trust_unavailable", exception.Code);
    }

    [Fact]
    public async Task AuthenticodeProbe_BoundsOutputBeforeJsonParsing()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string(
            'x',
            WindowsPowerShellAuthenticodeProbe.MaximumOutputCharacters + 1)));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await Assert.ThrowsAsync<WindowsPowerShellAuthenticodeProbe.ProbeOutputLimitException>(
            () => WindowsPowerShellAuthenticodeProbe.ReadBoundedAsync(
                reader,
                WindowsPowerShellAuthenticodeProbe.MaximumOutputCharacters,
                CancellationToken.None));
    }

    [Fact]
    public async Task WindowsProbe_DeniesSyntheticPackageWithoutPathInArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var package = CreatePackage(ValidManifest());
        var probe = new WindowsPowerShellAuthenticodeProbe();
        var startInfo = probe.CreateStartInfo(package);

        Assert.DoesNotContain(startInfo.ArgumentList, argument =>
            argument.Contains(package, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(package, startInfo.Environment["DCX_PACKAGE_REVIEW_PATH"]);
        Assert.DoesNotContain(startInfo.Environment.Keys, key =>
            key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("KEY", StringComparison.OrdinalIgnoreCase));

        var evidence = await probe.InspectAsync(package);

        Assert.Contains(evidence.Status, new[] { "NotSigned", "UnknownError" });
        Assert.Null(evidence.SignerSubject);
        Assert.Null(evidence.SignerCertificateSha256);

        var composed = await new MsixPackageTrustVerifier(probe)
            .VerifyAsync(package, CancellationToken.None);
        Assert.False(composed.SignatureValid);
    }

    [Fact]
    public async Task WindowsProbe_KillsTimedOutProbeWithStableFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var package = CreatePackage(ValidManifest());
        var probe = new WindowsPowerShellAuthenticodeProbe(
            WindowsPowerShellPath(),
            TimeSpan.FromMilliseconds(100),
            "Start-Sleep -Seconds 5");

        await AssertCodeAsync(
            "package_review_trust_unavailable",
            () => probe.InspectAsync(package));
    }

    [Fact]
    public async Task WindowsProbe_KillsOutputFloodWithStableFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var package = CreatePackage(ValidManifest());
        var probe = new WindowsPowerShellAuthenticodeProbe(
            WindowsPowerShellPath(),
            TimeSpan.FromSeconds(10),
            "[Console]::Out.Write(('x' * 9000))");

        await AssertCodeAsync(
            "package_review_trust_unavailable",
            () => probe.InspectAsync(package));
    }

    public static TheoryData<string> InvalidManifests => new()
    {
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" />",
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"DCx.AgenticBrowser\" Publisher=\"CN=DCx Test Publisher\" Version=\"1.2.3.4\"/><Identity Name=\"Other.Identity\" Publisher=\"CN=DCx Test Publisher\" Version=\"1.2.3.4\"/></Package>",
        "<!DOCTYPE Package [<!ENTITY xxe SYSTEM \"file:///Windows/win.ini\">]><Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"DCx.AgenticBrowser\" Publisher=\"&xxe;\" Version=\"1.2.3.4\"/></Package>",
        "<Package xmlns=\"http://schemas.microsoft.com/appx/2010/manifest\"><Identity Name=\"DCx.AgenticBrowser\" Publisher=\"CN=DCx Test Publisher\" Version=\"1.2.3.4\"/></Package>",
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"DCx.AgenticBrowser\" Publisher=\"CN=DCx Test Publisher\" Version=\"1.2.3\"/></Package>",
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreatePackage(
        string manifest,
        params (string Name, string Content)[] additionalEntries)
    {
        var path = Path.Combine(root, $"fixture-{Guid.NewGuid():N}.msix");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, MsixManifestInspector.ManifestName, manifest);
        WriteEntry(archive, "Assets/readme.txt", "synthetic fixture");
        foreach (var entry in additionalEntries) WriteEntry(archive, entry.Name, entry.Content);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ValidManifest() =>
        $"<Package xmlns=\"{MsixManifestInspector.FoundationNamespace}\"><Identity Name=\"DCx.AgenticBrowser\" Publisher=\"{Publisher}\" Version=\"1.2.3.4\" /></Package>";

    private static string WindowsPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PackageInstallReviewException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FixtureSignatureProbe(PackageAuthenticodeEvidence evidence)
        : IPackageAuthenticodeProbe
    {
        public Task<PackageAuthenticodeEvidence> InspectAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Path.IsPathFullyQualified(packagePath));
            return Task.FromResult(evidence);
        }
    }
}
