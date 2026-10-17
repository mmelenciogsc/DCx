using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace DCx.Core.Deployment;

public sealed record MsixManifestIdentity(
    string PackageIdentityName,
    string PublisherId,
    string Version);

public sealed record PackageAuthenticodeEvidence(
    string Status,
    string? SignerSubject,
    string? SignerCertificateSha256);

public interface IPackageAuthenticodeProbe
{
    Task<PackageAuthenticodeEvidence> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken);
}

public sealed class MsixManifestInspector
{
    public const int MaximumEntries = 10_000;
    public const long MaximumManifestBytes = 1024 * 1024;
    public const string ManifestName = "AppxManifest.xml";
    public const string FoundationNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    public async Task<MsixManifestIdentity> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var input = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is < 1 or > MaximumEntries)
            {
                throw InvalidManifest();
            }

            ZipArchiveEntry? manifest = null;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.FullName, ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(entry.FullName, ManifestName, StringComparison.Ordinal) || manifest is not null)
                {
                    throw InvalidManifest();
                }

                manifest = entry;
            }

            if (manifest is null || manifest.Length is < 1 or > MaximumManifestBytes ||
                manifest.CompressedLength is < 1 or > MaximumManifestBytes * 2)
            {
                throw InvalidManifest();
            }

            await using var manifestStream = manifest.Open();
            var settings = new XmlReaderSettings
            {
                Async = true,
                CloseInput = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(manifestStream, settings);
            var document = await XDocument.LoadAsync(
                    reader,
                    LoadOptions.None,
                    cancellationToken)
                .ConfigureAwait(false);
            var foundation = XNamespace.Get(FoundationNamespace);
            if (document.Root?.Name != foundation + "Package")
            {
                throw InvalidManifest();
            }

            var identities = document.Root.Elements(foundation + "Identity").ToArray();
            if (identities.Length != 1)
            {
                throw InvalidManifest();
            }

            var identity = identities[0];
            var name = (string?)identity.Attribute("Name");
            var publisher = (string?)identity.Attribute("Publisher");
            var version = (string?)identity.Attribute("Version");
            if (!IsIdentityName(name) || !IsPublisher(publisher) || string.IsNullOrEmpty(version))
            {
                throw InvalidManifest();
            }

            try
            {
                _ = Reliability.ControlledUpdateManifestCodec.ParsePackageVersion(version);
            }
            catch (Reliability.ControlledUpdateException)
            {
                throw InvalidManifest();
            }

            return new MsixManifestIdentity(name!, publisher!, version);
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
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                XmlException or NotSupportedException)
        {
            throw InvalidManifest();
        }
    }

    private static PackageInstallReviewException InvalidManifest() =>
        new("package_review_msix_manifest_invalid");

    private static bool IsIdentityName(string? value) =>
        value is { Length: >= 3 and <= 50 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsPublisher(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        value == value.Trim() && !value.Any(char.IsControl);
}

public sealed class MsixPackageTrustVerifier(
    IPackageAuthenticodeProbe signatureProbe,
    MsixManifestInspector? manifestInspector = null) : IPackageInstallTrustVerifier
{
    private readonly MsixManifestInspector manifestInspector = manifestInspector ?? new();

    public async Task<PackageInstallTrustEvidence> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signatureProbe);
        var identity = await manifestInspector.InspectAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        var signature = await signatureProbe.InspectAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        var certificateSha256 = signature.SignerCertificateSha256 ?? new string('0', 64);
        var signatureValid = string.Equals(signature.Status, "Valid", StringComparison.Ordinal) &&
            string.Equals(signature.SignerSubject, identity.PublisherId, StringComparison.Ordinal) &&
            IsLowerSha256(certificateSha256);
        return new PackageInstallTrustEvidence(
            signatureValid,
            identity.PackageIdentityName,
            identity.PublisherId,
            identity.Version,
            certificateSha256);
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class WindowsPowerShellAuthenticodeProbe : IPackageAuthenticodeProbe
{
    internal const int MaximumOutputCharacters = 8 * 1024;
    private const string PackagePathEnvironmentVariable = "DCX_PACKAGE_REVIEW_PATH";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Valid",
        "UnknownError",
        "NotSigned",
        "HashMismatch",
        "NotTrusted",
        "NotSupported",
        "Incompatible",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };
    private const string ProbeScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $packagePath = [Environment]::GetEnvironmentVariable('DCX_PACKAGE_REVIEW_PATH', 'Process')
        if ([string]::IsNullOrWhiteSpace($packagePath)) { exit 12 }
        $signature = Get-AuthenticodeSignature -LiteralPath $packagePath -ErrorAction Stop
        $subject = $null
        $certificateSha256 = $null
        if ($null -ne $signature.SignerCertificate) {
            $subject = [string]$signature.SignerCertificate.Subject
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $hash = $sha256.ComputeHash($signature.SignerCertificate.RawData)
                $certificateSha256 = ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
            }
            finally { $sha256.Dispose() }
        }
        $result = [ordered]@{
            schemaVersion = 1
            status = [string]$signature.Status
            signerSubject = $subject
            signerCertificateSha256 = $certificateSha256
        }
        [Console]::Out.Write(($result | ConvertTo-Json -Compress))
        """;

    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly string encodedCommand;

    public WindowsPowerShellAuthenticodeProbe(
        string? executablePath = null,
        TimeSpan? timeout = null)
        : this(
            executablePath ?? DefaultExecutablePath(),
            timeout ?? DefaultTimeout,
            ProbeScript,
            requireWindowsDefault: executablePath is null)
    {
    }

    internal WindowsPowerShellAuthenticodeProbe(
        string executablePath,
        TimeSpan timeout,
        string probeScript)
        : this(executablePath, timeout, probeScript, requireWindowsDefault: false)
    {
    }

    private WindowsPowerShellAuthenticodeProbe(
        string executablePath,
        TimeSpan timeout,
        string probeScript,
        bool requireWindowsDefault)
    {
        if (requireWindowsDefault && !OperatingSystem.IsWindows())
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        this.executablePath = executablePath;
        this.timeout = timeout;
        encodedCommand = string.IsNullOrWhiteSpace(probeScript)
            ? string.Empty
            : Convert.ToBase64String(Encoding.Unicode.GetBytes(probeScript));
        if (!File.Exists(this.executablePath) || this.timeout <= TimeSpan.Zero ||
            this.timeout > TimeSpan.FromSeconds(30) || string.IsNullOrEmpty(encodedCommand))
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }
    }

    public async Task<PackageAuthenticodeEvidence> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !Path.IsPathFullyQualified(packagePath) ||
            !File.Exists(packagePath))
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        var startInfo = CreateStartInfo(packagePath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new PackageInstallReviewException("package_review_trust_unavailable");
            }
        }
        catch (PackageInstallReviewException)
        {
            throw;
        }
        catch
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            MaximumOutputCharacters,
            deadline.Token);
        var standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumOutputCharacters,
            deadline.Token);
        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(deadline.Token),
                    standardOutput,
                    standardError)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (ProbeOutputLimitException)
        {
            TryKill(process);
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        if (process.ExitCode != 0)
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        return Decode(standardOutput.Result);
    }

    internal ProcessStartInfo CreateStartInfo(string packagePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "WINDIR");
        CopyEnvironment(startInfo, "ComSpec");
        CopyEnvironment(startInfo, "TEMP");
        CopyEnvironment(startInfo, "TMP");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["PSModulePath"] = Path.Combine(
                systemRoot,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "Modules");
        }

        startInfo.Environment[PackagePathEnvironmentVariable] = packagePath;
        return startInfo;
    }

    internal static PackageAuthenticodeEvidence Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumOutputCharacters)
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(value, JsonOptions);
            if (envelope is null || envelope.SchemaVersion != 1 ||
                !AllowedStatuses.Contains(envelope.Status) ||
                !IsOptionalSubject(envelope.SignerSubject) ||
                !IsOptionalSha256(envelope.SignerCertificateSha256) ||
                (string.Equals(envelope.Status, "Valid", StringComparison.Ordinal) &&
                 (envelope.SignerSubject is null || envelope.SignerCertificateSha256 is null)))
            {
                throw new PackageInstallReviewException("package_review_trust_unavailable");
            }

            return new PackageAuthenticodeEvidence(
                envelope.Status,
                envelope.SignerSubject,
                envelope.SignerCertificateSha256);
        }
        catch (PackageInstallReviewException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new PackageInstallReviewException("package_review_trust_unavailable");
        }
    }

    internal static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 1024));
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maximumCharacters)
            {
                throw new ProbeOutputLimitException();
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Failure to kill is reported only as the stable trust-unavailable state.
        }
    }

    private static bool IsOptionalSubject(string? value) =>
        value is null || (value.Length is > 0 and <= 512 &&
            value == value.Trim() && !value.Any(char.IsControl));

    private static bool IsOptionalSha256(string? value) =>
        value is null || (value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private sealed record SignatureEnvelope(
        int SchemaVersion,
        string Status,
        string? SignerSubject,
        string? SignerCertificateSha256);

    internal sealed class ProbeOutputLimitException : Exception;

    private static string DefaultExecutablePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
}
