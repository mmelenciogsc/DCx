using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class ControlledUpdateStagerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    private static readonly Uri ManifestUri =
        new("https://updates.example.test/stable/manifest.json");
    private static readonly Uri PackageUri =
        new("https://updates.example.test/stable/DCx-1.2.0.0.msix");

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-controlled-update-{Guid.NewGuid():N}");

    [Fact]
    public async Task SignedPackage_IsAdmittedOnceAndReverifiedWithoutInstallation()
    {
        var package = "synthetic signed MSIX fixture"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, package);
        var transport = new FixtureTransport(ManifestUri, envelope, PackageUri, package);
        var trust = new FixtureTrustVerifier(ValidTrust());
        var stager = new ControlledUpdateStager(
            transport, trust, key.ExportSubjectPublicKeyInfo());

        var first = await stager.CheckAndStageAsync(
            ManifestUri, "1.1.9.9", root, Now);
        var second = await stager.CheckAndStageAsync(
            ManifestUri, "1.1.9.9", root, Now);

        Assert.Equal("1.2.0.0", first.Version);
        Assert.Equal(first, second);
        Assert.Equal(package, await File.ReadAllBytesAsync(first.PackagePath));
        Assert.Equal(envelope, await File.ReadAllBytesAsync(first.SignedManifestPath));
        Assert.Equal(2, transport.ManifestDownloads);
        Assert.Equal(1, transport.PackageDownloads);
        Assert.Equal(2, trust.Verifications);
        Assert.DoesNotContain("installed", first.PackagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestPolicy_RejectsWrongSignatureDowngradeExpiryAndCrossOrigin()
    {
        var package = "update fixture"u8.ToArray();
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validEnvelope = Envelope(signer, package);
        var transport = new FixtureTransport(ManifestUri, validEnvelope, PackageUri, package);
        var stager = new ControlledUpdateStager(
            transport, new FixtureTrustVerifier(ValidTrust()), wrongKey.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_signature_invalid", () => stager.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "wrong-key"), Now));
        Assert.Equal(0, transport.PackageDownloads);

        var trusted = new ControlledUpdateStager(
            transport, new FixtureTrustVerifier(ValidTrust()), signer.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_not_newer", () => trusted.CheckAndStageAsync(
            ManifestUri, "1.2.0.0", Path.Combine(root, "same-version"), Now));

        var expiredManifest = Manifest(package) with
        {
            PublishedUtc = Now.AddDays(-2),
            ExpiresUtc = Now.AddDays(-1),
        };
        transport.Manifest = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(expiredManifest, signer));
        await AssertCodeAsync("update_manifest_expired", () => trusted.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "expired"), Now));

        var crossOrigin = Manifest(package) with
        {
            PackageUri = "https://attacker.example.test/DCx-1.2.0.0.msix",
        };
        transport.Manifest = ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(crossOrigin, signer));
        await AssertCodeAsync("update_origin_invalid", () => trusted.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "cross-origin"), Now));
        Assert.Equal(0, transport.PackageDownloads);
    }

    [Fact]
    public async Task PackageAdmission_RejectsDigestTrustAndExistingFileTampering()
    {
        var expected = "expected package"u8.ToArray();
        var changed = "tampered package"u8.ToArray();
        Assert.Equal(expected.Length, changed.Length);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Envelope(key, expected);
        var badTransport = new FixtureTransport(ManifestUri, envelope, PackageUri, changed);
        var stager = new ControlledUpdateStager(
            badTransport, new FixtureTrustVerifier(ValidTrust()), key.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_package_integrity_invalid", () => stager.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "digest"), Now));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "digest", "packages")));

        var goodTransport = new FixtureTransport(ManifestUri, envelope, PackageUri, expected);
        var invalidTrust = new FixtureTrustVerifier(ValidTrust() with { SignatureValid = false });
        var untrustedStager = new ControlledUpdateStager(
            goodTransport, invalidTrust, key.ExportSubjectPublicKeyInfo());
        await AssertCodeAsync("update_package_trust_invalid", () => untrustedStager.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "trust"), Now));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "trust", "packages")));

        var validStager = new ControlledUpdateStager(
            goodTransport, new FixtureTrustVerifier(ValidTrust()), key.ExportSubjectPublicKeyInfo());
        var admitted = await validStager.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "existing"), Now);
        var downloadsBeforeTamperCheck = goodTransport.PackageDownloads;
        await File.WriteAllBytesAsync(admitted.PackagePath, changed);
        await AssertCodeAsync("update_package_integrity_invalid", () => validStager.CheckAndStageAsync(
            ManifestUri, "1.1.0.0", Path.Combine(root, "existing"), Now));
        Assert.Equal(downloadsBeforeTamperCheck, goodTransport.PackageDownloads);
    }

    [Fact]
    public void Codec_RejectsNoncanonicalUnknownAndInvalidVersionManifests()
    {
        var package = "package"u8.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var canonical = Envelope(key, package);
        var padded = new byte[canonical.Length + 1];
        padded[0] = (byte)' ';
        canonical.CopyTo(padded, 1);
        AssertCode("update_envelope_noncanonical", () =>
            ControlledUpdateManifestCodec.Decode(padded));

        var text = System.Text.Encoding.UTF8.GetString(canonical);
        var unknown = System.Text.Encoding.UTF8.GetBytes(
            text.Insert(text.Length - 1, ",\"unknown\":true"));
        AssertCode("update_envelope_invalid", () =>
            ControlledUpdateManifestCodec.Decode(unknown));
        AssertCode("update_envelope_invalid", () =>
            ControlledUpdateManifestCodec.Decode("{}"u8));

        AssertCode("update_version_invalid", () =>
            ControlledUpdateManifestCodec.ParsePackageVersion("1.2.3"));
        AssertCode("update_version_invalid", () =>
            ControlledUpdateManifestCodec.ParsePackageVersion("1.02.3.4"));
    }

    [Fact]
    public async Task HttpTransport_EnforcesOriginMediaTypeSizeAndRedirectDenial()
    {
        var handler = new FixtureHttpHandler(request =>
        {
            var content = new ByteArrayContent("{}"u8.ToArray());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                HttpControlledUpdateTransport.ManifestMediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var transport = new HttpControlledUpdateTransport(
            new Uri("https://updates.example.test/"), handler);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(
            await transport.DownloadManifestAsync(ManifestUri, 100, default)));
        Assert.Equal(1, handler.Requests);

        await AssertCodeAsync("update_origin_invalid", () => transport.DownloadManifestAsync(
            new Uri("https://other.example.test/manifest.json"), 100, default));
        Assert.Equal(1, handler.Requests);

        handler.Response = _ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.test/manifest.json") },
        };
        await AssertCodeAsync("update_transport_failed", () =>
            transport.DownloadManifestAsync(ManifestUri, 100, default));

        handler.Response = _ =>
        {
            var content = new ByteArrayContent(new byte[101]);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                HttpControlledUpdateTransport.ManifestMediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        await AssertCodeAsync("update_manifest_oversized", () =>
            transport.DownloadManifestAsync(ManifestUri, 100, default));

        var package = "http package fixture"u8.ToArray();
        handler.Response = _ =>
        {
            var content = new ByteArrayContent(package);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/msix");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        using var destination = new MemoryStream();
        Assert.Equal(package.Length, await transport.DownloadPackageAsync(
            PackageUri, destination, package.Length, default));
        Assert.Equal(package, destination.ToArray());
        await AssertCodeAsync("update_package_oversized", () => transport.DownloadPackageAsync(
            PackageUri, new MemoryStream(), package.Length - 1, default));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static ControlledUpdateManifest Manifest(byte[] package) => new(
        ControlledUpdateManifestCodec.CurrentSchemaVersion,
        ControlledUpdateManifestCodec.ProductId,
        "1.2.0.0",
        Now,
        Now.AddDays(1),
        PackageUri.AbsoluteUri,
        package.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(package)),
        "DCx.AgenticBrowser",
        "CN=DCx Test Publisher");

    private static byte[] Envelope(ECDsa key, byte[] package) =>
        ControlledUpdateManifestCodec.Encode(
            ControlledUpdateManifestCodec.Sign(Manifest(package), key));

    private static UpdatePackageTrustEvidence ValidTrust() => new(
        true,
        "DCx.AgenticBrowser",
        "CN=DCx Test Publisher",
        "1.2.0.0");

    private static async Task AssertCodeAsync(
        string expected,
        Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FixtureTransport(
        Uri manifestUri,
        byte[] manifest,
        Uri packageUri,
        byte[] package) : IControlledUpdateTransport
    {
        public byte[] Manifest { get; set; } = manifest;
        public int ManifestDownloads { get; private set; }
        public int PackageDownloads { get; private set; }

        public Task<byte[]> DownloadManifestAsync(
            Uri uri,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(manifestUri, uri);
            Assert.True(Manifest.Length <= maximumBytes);
            ManifestDownloads++;
            return Task.FromResult(Manifest.ToArray());
        }

        public async Task<long> DownloadPackageAsync(
            Uri uri,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(packageUri, uri);
            Assert.True(package.LongLength <= maximumBytes);
            PackageDownloads++;
            await destination.WriteAsync(package, cancellationToken);
            return package.LongLength;
        }
    }

    private sealed class FixtureTrustVerifier(UpdatePackageTrustEvidence evidence)
        : IUpdatePackageTrustVerifier
    {
        public int Verifications { get; private set; }

        public Task<UpdatePackageTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(packagePath));
            Verifications++;
            return Task.FromResult(evidence);
        }
    }

    private sealed class FixtureHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var result = Response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }
}
