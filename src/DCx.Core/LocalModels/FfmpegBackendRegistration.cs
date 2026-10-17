using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Deployment;

namespace DCx.Core.LocalModels;

public sealed record ApprovedFfmpegBinding(
    string ExecutablePath,
    string ExecutableSha256,
    string LicensePath,
    string LicenseSha256,
    string AuthenticodeStatus,
    string? SignerSubject,
    string? SignerCertificateSha256,
    DateTimeOffset ApprovedUtc)
{
    public bool HasSameIdentity(ApprovedFfmpegBinding other) =>
        other is not null &&
        string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ExecutableSha256, other.ExecutableSha256, StringComparison.Ordinal) &&
        string.Equals(LicensePath, other.LicensePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(LicenseSha256, other.LicenseSha256, StringComparison.Ordinal) &&
        string.Equals(AuthenticodeStatus, other.AuthenticodeStatus, StringComparison.Ordinal) &&
        string.Equals(SignerSubject, other.SignerSubject, StringComparison.Ordinal) &&
        string.Equals(SignerCertificateSha256, other.SignerCertificateSha256, StringComparison.Ordinal);
}

public sealed record FfmpegBackendReview(
    ApprovedFfmpegBinding Binding,
    string LicenseText,
    long ExecutableBytes);

public sealed class FfmpegBackendReviewer
{
    public const long MaximumExecutableBytes = 512L * 1024 * 1024;
    public const int MaximumLicenseBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IPackageAuthenticodeProbe authenticodeProbe;

    public FfmpegBackendReviewer(IPackageAuthenticodeProbe? authenticodeProbe = null) =>
        this.authenticodeProbe = authenticodeProbe ?? new WindowsPowerShellAuthenticodeProbe();

    public async Task<FfmpegBackendReview> ReviewAsync(
        string executablePath,
        string licensePath,
        DateTimeOffset reviewedUtc,
        CancellationToken cancellationToken = default)
    {
        var executable = ValidateRegularLocalFile(executablePath, "FFmpeg executable", ".exe");
        var license = ValidateLicenseFile(licensePath);
        if (string.Equals(executable, license, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("FFmpeg executable and license files must be distinct.");

        await using var executableLease = OpenLease(executable, MaximumExecutableBytes, "FFmpeg executable");
        await using var licenseLease = OpenLease(license, MaximumLicenseBytes, "FFmpeg license");
        var executableDigestTask = HashAsync(executableLease, cancellationToken);
        var licenseBytesTask = ReadAllAsync(licenseLease, cancellationToken);
        var signatureTask = authenticodeProbe.InspectAsync(executable, cancellationToken);
        await Task.WhenAll(executableDigestTask, licenseBytesTask, signatureTask).ConfigureAwait(false);
        var licenseBytes = await licenseBytesTask.ConfigureAwait(false);
        var licenseDigest = Convert.ToHexStringLower(SHA256.HashData(licenseBytes));
        var licenseText = DecodeText(licenseBytes);
        var signature = await signatureTask.ConfigureAwait(false);
        var binding = new ApprovedFfmpegBinding(
            executable,
            await executableDigestTask.ConfigureAwait(false),
            license,
            licenseDigest,
            signature.Status,
            signature.SignerSubject,
            signature.SignerCertificateSha256,
            reviewedUtc.ToUniversalTime());
        FfmpegBackendSelectionStore.ValidateBinding(binding);
        return new FfmpegBackendReview(binding, licenseText, executableLease.Length);
    }

    private static string ValidateRegularLocalFile(string path, string label, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path.Contains('\0')) throw new ArgumentException($"{label} path is invalid.", nameof(path));
        var canonical = Path.GetFullPath(path.Trim());
        if (canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(canonical), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{label} must be a local {extension} file.", nameof(path));
        RejectReparseChain(canonical, label);
        return canonical;
    }

    private static string ValidateLicenseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path.Contains('\0')) throw new ArgumentException("FFmpeg license path is invalid.", nameof(path));
        var canonical = Path.GetFullPath(path.Trim());
        var name = Path.GetFileName(canonical);
        var extension = Path.GetExtension(canonical);
        if (canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("FFmpeg license must be a local text, Markdown, LICENSE, COPYING or NOTICE file.", nameof(path));
        RejectReparseChain(canonical, "FFmpeg license");
        return canonical;
    }

    private static void RejectReparseChain(string path, string label)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new ArgumentException($"{label} must be an existing regular file.", nameof(path));
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"{label} cannot cross a reparse directory.", nameof(path));
    }

    private static FileStream OpenLease(string path, long maximumBytes, string label)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            stream.Dispose();
            throw new ArgumentException($"{label} is empty or exceeds its size limit.", nameof(path));
        }
        return stream;
    }

    private static async Task<string> HashAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAllAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            var text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF')
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Any(character =>
                    char.IsControl(character) && character is not ('\n' or '\t')))
                throw new InvalidOperationException("The FFmpeg license is empty or contains unsupported control characters.");
            return text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("The FFmpeg license is not valid UTF-8 text.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed record FfmpegBackendSelectionState(int SchemaVersion, ApprovedFfmpegBinding? Backend);

public sealed class FfmpegBackendSelectionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStateBytes = 24 * 1024;
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Valid", "UnknownError", "NotSigned", "HashMismatch", "NotTrusted", "NotSupported", "Incompatible",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };
    private readonly string root;
    private readonly string statePath;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private FfmpegBackendSelectionStore(string root)
    {
        this.root = root;
        statePath = Path.Combine(root, "ffmpeg.json");
    }

    public static async Task<FfmpegBackendSelectionStore> OpenAsync(string applicationDataRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        var applicationRoot = Path.GetFullPath(applicationDataRoot);
        if (Path.GetPathRoot(applicationRoot)?.TrimEnd(Path.DirectorySeparatorChar) == applicationRoot.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException("The application-data root cannot be a volume root.", nameof(applicationDataRoot));
        Directory.CreateDirectory(applicationRoot);
        RejectReparse(applicationRoot);
        var root = Path.Combine(applicationRoot, "local-models");
        Directory.CreateDirectory(root);
        RejectReparse(root);
        var store = new FfmpegBackendSelectionStore(root);
        _ = await store.ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<FfmpegBackendSelectionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false); }
        finally { mutationLock.Release(); }
    }

    public async Task SetAsync(ApprovedFfmpegBinding binding, bool explicitlyApproved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!explicitlyApproved) throw new InvalidOperationException("Selecting FFmpeg requires explicit approval.");
        ValidateBinding(binding);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteUnsafeAsync(new(CurrentSchemaVersion, binding), cancellationToken).ConfigureAwait(false); }
        finally { mutationLock.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteUnsafeAsync(new(CurrentSchemaVersion, null), cancellationToken).ConfigureAwait(false); }
        finally { mutationLock.Release(); }
    }

    internal static void ValidateBinding(ApprovedFfmpegBinding binding)
    {
        ValidatePath(binding.ExecutablePath, ".exe", "executable");
        ValidatePath(binding.LicensePath, null, "license");
        var licenseName = Path.GetFileName(binding.LicensePath);
        var licenseExtension = Path.GetExtension(binding.LicensePath);
        if (!string.Equals(licenseExtension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(licenseExtension, ".md", StringComparison.OrdinalIgnoreCase) &&
            !licenseName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
            !licenseName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) &&
            !licenseName.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stored FFmpeg license path is invalid.");
        if (string.Equals(binding.ExecutablePath, binding.LicensePath, StringComparison.OrdinalIgnoreCase) ||
            !IsDigest(binding.ExecutableSha256) || !IsDigest(binding.LicenseSha256) ||
            !AllowedStatuses.Contains(binding.AuthenticodeStatus) || binding.ApprovedUtc == default ||
            binding.ApprovedUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("The stored FFmpeg binding is invalid.");
        if (binding.AuthenticodeStatus == "Valid")
        {
            if (!IsSubject(binding.SignerSubject) || !IsDigest(binding.SignerCertificateSha256))
                throw new InvalidOperationException("The stored signed FFmpeg evidence is incomplete.");
        }
        else if ((binding.SignerSubject is null) != (binding.SignerCertificateSha256 is null) ||
                 binding.SignerSubject is not null && !IsSubject(binding.SignerSubject) ||
                 binding.SignerCertificateSha256 is not null && !IsDigest(binding.SignerCertificateSha256))
            throw new InvalidOperationException("The stored unsigned FFmpeg evidence is inconsistent.");
    }

    private async Task<FfmpegBackendSelectionState> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        ValidateStatePath();
        if (!File.Exists(statePath)) return new(CurrentSchemaVersion, null);
        var file = new FileInfo(statePath);
        if (file.Length is <= 0 or > MaximumStateBytes) throw new InvalidOperationException("The FFmpeg selection file is outside its size limit.");
        try
        {
            await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<FfmpegBackendSelectionState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (state is null || state.SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException("The FFmpeg selection schema is unsupported.");
            if (state.Backend is not null) ValidateBinding(state.Backend);
            return state;
        }
        catch (JsonException exception) { throw new InvalidOperationException("The FFmpeg selection file is invalid.", exception); }
    }

    private async Task WriteUnsafeAsync(FfmpegBackendSelectionState state, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (content.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidOperationException("The FFmpeg selection exceeds its size limit.");
        var temporary = Path.Combine(root, $"ffmpeg-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ValidateStatePath()
    {
        var entries = Directory.GetFileSystemEntries(root, "ffmpeg.json");
        if (entries.Length > 1 || entries.Length == 1 &&
            (!string.Equals(entries[0], statePath, StringComparison.Ordinal) ||
             (File.GetAttributes(entries[0]) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
            throw new InvalidOperationException("The FFmpeg selection path is unsafe.");
    }

    private static void ValidatePath(string path, string? extension, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
            path.Contains('\0') || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            extension is not null && !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The stored FFmpeg {label} path is invalid.");
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSubject(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && value == value.Trim() && !value.Any(char.IsControl);

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The FFmpeg settings path cannot be a reparse point.");
    }
}
