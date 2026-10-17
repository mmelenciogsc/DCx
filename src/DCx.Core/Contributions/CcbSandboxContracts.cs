using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.Contributions;

public sealed record CcbSandboxExecutionRequest(
    string ArtifactSha256,
    string WorkspaceSha256,
    string TestSuiteSha256,
    string WorkspacePath,
    string Entrypoint,
    ReadOnlyMemory<byte> Input);

public sealed record CcbSandboxExecutionResult(
    bool Passed,
    string PolicyVersion,
    string ArtifactSha256,
    string WorkspaceSha256,
    string LauncherSha256,
    string BackendSha256,
    string RuntimeSha256,
    string TestSuiteSha256,
    string? OutputSha256,
    string EvidenceSha256,
    string? FailureCode);

public interface ICcbSandboxExecutor
{
    Task<CcbSandboxExecutionResult> ExecuteAsync(
        CcbSandboxExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public static class CcbSandboxEvidence
{
    public const string CurrentPolicyVersion = "dcx-ccb-bwrap-v1";
    public const int MaximumInputBytes = 1024 * 1024;

    public static CcbSandboxExecutionResult Create(
        CcbSandboxExecutionRequest request,
        string launcherSha256,
        string backendSha256,
        string runtimeSha256,
        ReadOnlySpan<byte> output)
    {
        ValidateRequest(request);
        ValidateSha256(launcherSha256, nameof(launcherSha256));
        ValidateSha256(backendSha256, nameof(backendSha256));
        ValidateSha256(runtimeSha256, nameof(runtimeSha256));
        var outputSha256 = Convert.ToHexStringLower(SHA256.HashData(output));
        var evidenceSha256 = EvidenceDigest(
            request.ArtifactSha256,
            request.WorkspaceSha256,
            launcherSha256,
            backendSha256,
            runtimeSha256,
            request.TestSuiteSha256,
            outputSha256,
            passed: true,
            failureCode: null);
        return new CcbSandboxExecutionResult(
            true,
            CurrentPolicyVersion,
            request.ArtifactSha256.ToLowerInvariant(),
            request.WorkspaceSha256.ToLowerInvariant(),
            launcherSha256.ToLowerInvariant(),
            backendSha256.ToLowerInvariant(),
            runtimeSha256.ToLowerInvariant(),
            request.TestSuiteSha256.ToLowerInvariant(),
            outputSha256,
            evidenceSha256,
            null);
    }

    public static CcbSandboxExecutionResult Fail(
        CcbSandboxExecutionRequest request,
        string launcherSha256,
        string backendSha256,
        string runtimeSha256,
        string failureCode)
    {
        ValidateRequest(request);
        ValidateSha256(launcherSha256, nameof(launcherSha256));
        ValidateSha256(backendSha256, nameof(backendSha256));
        ValidateSha256(runtimeSha256, nameof(runtimeSha256));
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 64 ||
            failureCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("The sandbox failure code is invalid.", nameof(failureCode));
        }

        var evidenceSha256 = EvidenceDigest(
            request.ArtifactSha256,
            request.WorkspaceSha256,
            launcherSha256,
            backendSha256,
            runtimeSha256,
            request.TestSuiteSha256,
            outputSha256: null,
            passed: false,
            failureCode);
        return new CcbSandboxExecutionResult(
            false,
            CurrentPolicyVersion,
            request.ArtifactSha256.ToLowerInvariant(),
            request.WorkspaceSha256.ToLowerInvariant(),
            launcherSha256.ToLowerInvariant(),
            backendSha256.ToLowerInvariant(),
            runtimeSha256.ToLowerInvariant(),
            request.TestSuiteSha256.ToLowerInvariant(),
            null,
            evidenceSha256,
            failureCode);
    }

    public static void ValidateRequest(CcbSandboxExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSha256(request.ArtifactSha256, nameof(request.ArtifactSha256));
        ValidateSha256(request.WorkspaceSha256, nameof(request.WorkspaceSha256));
        ValidateSha256(request.TestSuiteSha256, nameof(request.TestSuiteSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Entrypoint);
        if (request.Entrypoint.Length > 256 || request.Entrypoint.Contains('\0') ||
            request.Entrypoint.Contains('\\') || Path.IsPathRooted(request.Entrypoint) ||
            request.Entrypoint.Split('/').Any(part => part is "" or "." or "..") ||
            !request.Entrypoint.EndsWith(".sh", StringComparison.Ordinal) ||
            request.Input.Length > MaximumInputBytes)
        {
            throw new ArgumentException("The sandbox entrypoint or input is outside policy.", nameof(request));
        }
    }

    public static void VerifyBoundResult(
        CcbSandboxExecutionRequest request,
        CcbSandboxExecutionResult result)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.PolicyVersion, CurrentPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(result.ArtifactSha256, request.ArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.WorkspaceSha256, request.WorkspaceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.TestSuiteSha256, request.TestSuiteSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContributionPolicyException("ccb_sandbox_evidence_binding_invalid");
        }

        ValidateSha256(result.LauncherSha256, nameof(result.LauncherSha256));
        ValidateSha256(result.BackendSha256, nameof(result.BackendSha256));
        ValidateSha256(result.RuntimeSha256, nameof(result.RuntimeSha256));
        ValidateSha256(result.EvidenceSha256, nameof(result.EvidenceSha256));
        if (result.Passed != (result.FailureCode is null) ||
            result.Passed != (result.OutputSha256 is not null))
        {
            throw new ContributionPolicyException("ccb_sandbox_evidence_state_invalid");
        }

        if (result.OutputSha256 is not null)
        {
            ValidateSha256(result.OutputSha256, nameof(result.OutputSha256));
        }

        var expected = EvidenceDigest(
            result.ArtifactSha256,
            result.WorkspaceSha256,
            result.LauncherSha256,
            result.BackendSha256,
            result.RuntimeSha256,
            result.TestSuiteSha256,
            result.OutputSha256,
            result.Passed,
            result.FailureCode);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(result.EvidenceSha256)))
        {
            throw new ContributionPolicyException("ccb_sandbox_evidence_digest_invalid");
        }
    }

    private static string EvidenceDigest(
        string artifactSha256,
        string workspaceSha256,
        string launcherSha256,
        string backendSha256,
        string runtimeSha256,
        string testSuiteSha256,
        string? outputSha256,
        bool passed,
        string? failureCode)
    {
        var canonical = string.Join('\0',
            CurrentPolicyVersion,
            artifactSha256.ToLowerInvariant(),
            workspaceSha256.ToLowerInvariant(),
            launcherSha256.ToLowerInvariant(),
            backendSha256.ToLowerInvariant(),
            runtimeSha256.ToLowerInvariant(),
            testSuiteSha256.ToLowerInvariant(),
            outputSha256?.ToLowerInvariant() ?? string.Empty,
            passed ? "pass" : "fail",
            failureCode ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static async Task<string> ComputeWorkspaceSha256Async(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(root) || IsLink(root))
        {
            throw new ContributionPolicyException("ccb_sandbox_workspace_denied");
        }

        var files = new List<(string Relative, string FullPath, long Size)>();
        var directories = new Stack<string>();
        directories.Push(root);
        long totalBytes = 0;
        var entryCount = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entryCount > 256)
                {
                    throw new ContributionPolicyException("ccb_sandbox_workspace_schema_denied");
                }

                if (IsLink(entry)) throw new ContributionPolicyException("ccb_sandbox_workspace_link_denied");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                var file = new FileInfo(entry);
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                if (!IsSafeRelativePath(relative))
                {
                    throw new ContributionPolicyException("ccb_sandbox_workspace_schema_denied");
                }

                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > 64L * 1024 * 1024)
                {
                    throw new ContributionPolicyException("ccb_sandbox_workspace_size_denied");
                }

                files.Add((relative, file.FullName, file.Length));
            }
        }

        if (files.Count == 0) throw new ContributionPolicyException("ccb_sandbox_workspace_empty");
        using var canonical = new MemoryStream();
        foreach (var file in files.OrderBy(item => item.Relative, StringComparer.Ordinal))
        {
            await using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Size) throw new ContributionPolicyException("ccb_sandbox_workspace_changed");
            var digest = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var line = Encoding.UTF8.GetBytes($"{digest}  ./{file.Relative}\n");
            await canonical.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))));
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafeRelativePath(string value) =>
        value.Length is > 0 and <= 512 &&
        value.Split('/').All(part => part is not "" and not "." and not "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/');

    private static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A sandbox digest must be SHA-256 hex.", parameterName);
        }
    }
}
