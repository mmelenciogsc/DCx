using System.Security.Cryptography;
using System.Text;
using DCx.Core.Deployment;

namespace DCx.Core.LocalModels;

public sealed record OllamaRuntimeFileBinding(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record ApprovedOllamaRuntimeBinding(
    string RuntimeRoot,
    string RuntimeProfile,
    string ExecutableRelativePath,
    IReadOnlyList<OllamaRuntimeFileBinding> Files,
    string TreeSha256,
    string AuthenticodeStatus,
    string? SignerSubject,
    string? SignerCertificateSha256,
    DateTimeOffset ReviewedUtc)
{
    public string ExecutablePath => Path.Combine(RuntimeRoot, ExecutableRelativePath);
}

public sealed record OllamaRuntimeReview(
    ApprovedOllamaRuntimeBinding Binding,
    long TotalBytes,
    string OfficialLicenseUri);

public sealed class OllamaRuntimeReviewer
{
    public const int MaximumFiles = 512;
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumTreeBytes = 8L * 1024 * 1024 * 1024;
    public const string OfficialLicenseUri = "https://github.com/ollama/ollama/blob/main/LICENSE";
    public const string CompleteTreeProfile = "complete-tree-v1";
    public const string NvidiaCudaV13Profile = "nvidia-cuda-v13";

    private readonly IPackageAuthenticodeProbe authenticodeProbe;

    public OllamaRuntimeReviewer(IPackageAuthenticodeProbe? authenticodeProbe = null) =>
        this.authenticodeProbe = authenticodeProbe ?? new WindowsPowerShellAuthenticodeProbe();

    public async Task<OllamaRuntimeReview> ReviewAsync(
        string executablePath,
        DateTimeOffset reviewedUtc,
        CancellationToken cancellationToken = default) =>
        await ReviewAsync(
            executablePath,
            CompleteTreeProfile,
            reviewedUtc,
            cancellationToken).ConfigureAwait(false);

    public async Task<OllamaRuntimeReview> ReviewAsync(
        string executablePath,
        string runtimeProfile,
        DateTimeOffset reviewedUtc,
        CancellationToken cancellationToken = default)
    {
        var executable = ValidateExecutable(executablePath);
        var root = Path.GetDirectoryName(executable)!;
        ValidateRuntimeProfile(runtimeProfile);
        var executableRelativePath = NormalizeRelativePath(Path.GetRelativePath(root, executable));
        var paths = EnumerateRuntimeFiles(root, runtimeProfile);
        var files = new List<OllamaRuntimeFileBinding>(paths.Count);
        long totalBytes = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var lease = OpenReadLease(path);
            totalBytes = checked(totalBytes + lease.Length);
            if (totalBytes > MaximumTreeBytes)
                throw new InvalidOperationException("The Ollama runtime tree exceeds its byte limit.");
            var digest = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
            files.Add(new(
                NormalizeRelativePath(Path.GetRelativePath(root, path)),
                lease.Length,
                digest));
        }

        EnsureExactRuntimeInventory(root, runtimeProfile, files);
        var signature = await authenticodeProbe.InspectAsync(executable, cancellationToken).ConfigureAwait(false);
        var binding = new ApprovedOllamaRuntimeBinding(
            root,
            runtimeProfile,
            executableRelativePath,
            files.AsReadOnly(),
            ComputeTreeDigest(runtimeProfile, executableRelativePath, files),
            signature.Status,
            signature.SignerSubject,
            signature.SignerCertificateSha256,
            reviewedUtc.ToUniversalTime());
        ValidateBinding(binding);
        return new(binding, totalBytes, OfficialLicenseUri);
    }

    internal static void ValidateBinding(ApprovedOllamaRuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var root = ValidateRuntimeRoot(binding.RuntimeRoot);
        ValidateRuntimeProfile(binding.RuntimeProfile);
        if (binding.ReviewedUtc == default || binding.ReviewedUtc.Offset != TimeSpan.Zero ||
            !IsDigest(binding.TreeSha256) || binding.Files is null ||
            binding.Files.Count is < 1 or > MaximumFiles)
            throw new InvalidOperationException("The reviewed Ollama runtime binding is invalid.");

        var executableRelative = NormalizeRelativePath(binding.ExecutableRelativePath);
        if (!string.Equals(Path.GetExtension(executableRelative), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed Ollama executable identity is invalid.");

        var seen = new HashSet<string>(PathComparer);
        long totalBytes = 0;
        foreach (var file in binding.Files)
        {
            if (file is null || file.Length is <= 0 or > MaximumFileBytes || !IsDigest(file.Sha256))
                throw new InvalidOperationException("The reviewed Ollama runtime file binding is invalid.");
            var relative = NormalizeRelativePath(file.RelativePath);
            if (!string.Equals(relative, file.RelativePath, StringComparison.Ordinal) || !seen.Add(relative))
                throw new InvalidOperationException("The reviewed Ollama runtime file inventory is invalid.");
            _ = ResolveContained(root, relative);
            totalBytes = checked(totalBytes + file.Length);
        }
        if (totalBytes > MaximumTreeBytes || !seen.Contains(executableRelative) ||
            binding.RuntimeProfile == NvidiaCudaV13Profile &&
            (!seen.Contains("lib/ollama/llama-server.exe") ||
             !seen.Contains("lib/ollama/cuda_v13/ggml-cuda.dll")) ||
            !string.Equals(
                ComputeTreeDigest(binding.RuntimeProfile, executableRelative, binding.Files),
                binding.TreeSha256,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The reviewed Ollama runtime tree identity is invalid.");

        var allowedStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Valid", "UnknownError", "NotSigned", "HashMismatch", "NotTrusted", "NotSupported", "Incompatible",
        };
        if (!allowedStatuses.Contains(binding.AuthenticodeStatus))
            throw new InvalidOperationException("The reviewed Ollama signature status is invalid.");
        if (binding.AuthenticodeStatus == "Valid")
        {
            if (!IsBoundedText(binding.SignerSubject, 1024) || !IsDigest(binding.SignerCertificateSha256))
                throw new InvalidOperationException("The reviewed signed Ollama evidence is incomplete.");
        }
        else if (binding.SignerSubject is not null || binding.SignerCertificateSha256 is not null)
        {
            throw new InvalidOperationException("The reviewed Ollama signature evidence is inconsistent.");
        }
    }

    internal static IReadOnlyList<string> EnumerateRuntimeFiles(string runtimeRoot, string runtimeProfile)
    {
        var root = ValidateRuntimeRoot(runtimeRoot);
        ValidateRuntimeProfile(runtimeProfile);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparse(directory, "Ollama runtime directory");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var fullPath = Path.GetFullPath(entry);
                RequireContained(root, fullPath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The Ollama runtime tree contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (ShouldTraverseDirectory(root, fullPath, runtimeProfile)) pending.Push(fullPath);
                    continue;
                }
                if (files.Count >= MaximumFiles)
                    throw new InvalidOperationException("The Ollama runtime tree exceeds its file-count limit.");
                files.Add(fullPath);
            }
        }
        files.Sort((first, second) => StringComparer.Ordinal.Compare(
            NormalizeRelativePath(Path.GetRelativePath(root, first)),
            NormalizeRelativePath(Path.GetRelativePath(root, second))));
        if (files.Count == 0) throw new InvalidOperationException("The Ollama runtime tree is empty.");
        return files;
    }

    internal static FileStream OpenReadLease(string path)
    {
        RejectReparse(path, "Ollama runtime file");
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumFileBytes)
        {
            stream.Dispose();
            throw new InvalidOperationException("An Ollama runtime file is empty or exceeds its byte limit.");
        }
        return stream;
    }

    internal static string ResolveContained(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var platformRelative = normalized.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, platformRelative));
        RequireContained(root, fullPath);
        return fullPath;
    }

    internal static string ComputeTreeDigest(
        string runtimeProfile,
        string executableRelativePath,
        IReadOnlyList<OllamaRuntimeFileBinding> files)
    {
        ValidateRuntimeProfile(runtimeProfile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "dcx-ollama-runtime-tree-v1\n");
        Append(hash, runtimeProfile);
        Append(hash, "\n");
        Append(hash, NormalizeRelativePath(executableRelativePath));
        Append(hash, "\n");
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            Append(hash, NormalizeRelativePath(file.RelativePath));
            Append(hash, "\0");
            Append(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, "\0");
            Append(hash, file.Sha256);
            Append(hash, "\n");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static string ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0'))
            throw new ArgumentException("The Ollama executable path is invalid.", nameof(path));
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ollama must be an existing local .exe file.", nameof(path));
        RejectReparse(fullPath, "Ollama executable");
        return fullPath;
    }

    private static string ValidateRuntimeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0'))
            throw new InvalidOperationException("The Ollama runtime root is invalid.");
        var root = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Directory.Exists(root) || string.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                PathComparison))
            throw new InvalidOperationException("The Ollama runtime root is invalid.");
        RejectReparse(root, "Ollama runtime root");
        return root;
    }

    private static void EnsureExactRuntimeInventory(
        string root,
        string runtimeProfile,
        IReadOnlyList<OllamaRuntimeFileBinding> reviewed)
    {
        var actual = EnumerateRuntimeFiles(root, runtimeProfile)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .ToArray();
        var expected = reviewed.Select(item => item.RelativePath).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("The Ollama runtime tree changed during review.");
    }

    private static bool ShouldTraverseDirectory(string root, string directory, string runtimeProfile)
    {
        if (runtimeProfile == CompleteTreeProfile) return true;
        var relative = NormalizeRelativePath(Path.GetRelativePath(root, directory));
        return relative is "lib" or "lib/ollama" or "lib/ollama/cuda_v13" ||
            relative.StartsWith("lib/ollama/cuda_v13/", StringComparison.Ordinal);
    }

    private static void ValidateRuntimeProfile(string runtimeProfile)
    {
        if (runtimeProfile is not (CompleteTreeProfile or NvidiaCudaV13Profile))
            throw new InvalidOperationException("The reviewed Ollama runtime profile is unsupported.");
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0'))
            throw new InvalidOperationException("An Ollama runtime relative path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized[0] == '/' || normalized[^1] == '/' ||
            Path.IsPathFullyQualified(path) || normalized.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
            throw new InvalidOperationException("An Ollama runtime relative path is invalid.");
        return normalized;
    }

    private static void RejectReparse(string path, string label)
    {
        var info = File.Exists(path) ? new FileInfo(path) as FileSystemInfo : new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"The {label} is missing or unsafe.");
        for (var parent = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent;
             parent is not null;
             parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"The {label} crosses a reparse point.");
        }
    }

    private static void RequireContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("An Ollama runtime path escaped its reviewed root.");
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBoundedText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim() &&
        !value.Any(char.IsControl);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
