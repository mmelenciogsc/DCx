using System.Diagnostics;
using System.Security.Cryptography;
using DCx.Core.Contributions;

namespace DCx.GlobalService;

public sealed record BubblewrapCcbSandboxOptions(
    string BashPath,
    string BashSha256,
    string LauncherPath,
    string LauncherSha256,
    string BubblewrapPath,
    string BubblewrapSha256,
    string RuntimePath,
    string RuntimeSha256,
    string QuarantineRoot,
    TimeSpan Timeout,
    int MaximumOutputBytes = 1024 * 1024,
    int MaximumDiagnosticBytes = 64 * 1024);

public sealed class BubblewrapCcbSandboxExecutor : ICcbSandboxExecutor
{
    private readonly BubblewrapCcbSandboxOptions options;
    private readonly string bashPath;
    private readonly string launcherPath;
    private readonly string bubblewrapPath;
    private readonly string runtimePath;
    private readonly string quarantineRoot;

    public BubblewrapCcbSandboxExecutor(BubblewrapCcbSandboxOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        bashPath = ValidateTool(options.BashPath, options.BashSha256, "Bash");
        launcherPath = ValidateTool(options.LauncherPath, options.LauncherSha256, "sandbox launcher");
        bubblewrapPath = ValidateTool(options.BubblewrapPath, options.BubblewrapSha256, "Bubblewrap");
        runtimePath = ValidateTool(options.RuntimePath, options.RuntimeSha256, "sandbox runtime");
        quarantineRoot = Path.GetFullPath(options.QuarantineRoot);
        if (!OperatingSystem.IsLinux() || !Directory.Exists(quarantineRoot) || IsLink(quarantineRoot) ||
            options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromSeconds(30) ||
            options.MaximumOutputBytes is <= 0 or > CcbSandboxEvidence.MaximumInputBytes ||
            options.MaximumDiagnosticBytes is <= 0 or > 1024 * 1024)
        {
            throw new PlatformNotSupportedException("The CCB sandbox requires a bounded Linux quarantine and toolchain.");
        }
    }

    public async Task<CcbSandboxExecutionResult> ExecuteAsync(
        CcbSandboxExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        CcbSandboxEvidence.ValidateRequest(request);
        var workspace = RequireDescendantDirectory(request.WorkspacePath, quarantineRoot);
        var workspaceSha256 = await CcbSandboxEvidence.ComputeWorkspaceSha256Async(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(workspaceSha256),
                Convert.FromHexString(request.WorkspaceSha256)))
        {
            throw new ContributionPolicyException("ccb_sandbox_workspace_digest_invalid");
        }

        await VerifyDigestAsync(bashPath, options.BashSha256, cancellationToken).ConfigureAwait(false);
        await VerifyDigestAsync(launcherPath, options.LauncherSha256, cancellationToken).ConfigureAwait(false);
        await VerifyDigestAsync(bubblewrapPath, options.BubblewrapSha256, cancellationToken).ConfigureAwait(false);
        await VerifyDigestAsync(runtimePath, options.RuntimeSha256, cancellationToken).ConfigureAwait(false);

        var operationRoot = Path.Combine(quarantineRoot, ".operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        var inputPath = Path.Combine(operationRoot, "request.bin");
        await File.WriteAllBytesAsync(inputPath, request.Input.ToArray(), cancellationToken).ConfigureAwait(false);
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(bashPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = operationRoot,
            };
            startInfo.Environment.Clear();
            startInfo.Environment["PATH"] = "/usr/bin:/bin";
            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["TMPDIR"] = operationRoot;
            foreach (var argument in new[]
                     {
                         launcherPath,
                         quarantineRoot,
                         workspace,
                         request.WorkspaceSha256,
                         request.Entrypoint,
                         inputPath,
                         bubblewrapPath,
                         options.BubblewrapSha256,
                         runtimePath,
                         options.RuntimeSha256,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The CCB sandbox launcher could not start.");
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaximumOutputBytes);
            var diagnosticTask = DrainBoundedAsync(process.StandardError.BaseStream, options.MaximumDiagnosticBytes);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await ObserveAsync(outputTask, diagnosticTask).ConfigureAwait(false);
                return CcbSandboxEvidence.Fail(
                    request, options.LauncherSha256, options.BubblewrapSha256, options.RuntimeSha256, "wall_timeout");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await ObserveAsync(outputTask, diagnosticTask).ConfigureAwait(false);
                throw;
            }

            byte[] output;
            try
            {
                output = await outputTask.ConfigureAwait(false);
                await diagnosticTask.ConfigureAwait(false);
            }
            catch (SandboxOutputLimitException)
            {
                Kill(process);
                return CcbSandboxEvidence.Fail(
                    request, options.LauncherSha256, options.BubblewrapSha256, options.RuntimeSha256, "output_limit");
            }

            return process.ExitCode == 0
                ? CcbSandboxEvidence.Create(
                    request, options.LauncherSha256, options.BubblewrapSha256, options.RuntimeSha256, output)
                : CcbSandboxEvidence.Fail(
                    request,
                    options.LauncherSha256,
                    options.BubblewrapSha256,
                    options.RuntimeSha256,
                    process.ExitCode switch
                    {
                        65 => "workspace_denied",
                        70 => "sandbox_timeout",
                        71 => "output_limit",
                        72 => "tool_failure",
                        _ => "sandbox_failure",
                    });
        }
        finally
        {
            if (process is { HasExited: false }) Kill(process);
            process?.Dispose();
            if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, recursive: true);
        }
    }

    private static string ValidateTool(string path, string digest, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException($"The {label} digest is invalid.", nameof(digest));
        }

        var canonical = Path.GetFullPath(path);
        var file = new FileInfo(canonical);
        if (!file.Exists || file.Length <= 0 || IsLink(canonical))
        {
            throw new ArgumentException($"The {label} must be a regular file.", nameof(path));
        }

        return canonical;
    }

    private static string RequireDescendantDirectory(string path, string root)
    {
        var canonical = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, canonical);
        if (!Directory.Exists(canonical) || IsLink(canonical) || Path.IsPathRooted(relative) ||
            relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ContributionPolicyException("ccb_sandbox_workspace_denied");
        }

        return canonical;
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static async Task VerifyDigestAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContributionPolicyException("ccb_sandbox_toolchain_digest_invalid");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximumBytes) throw new SandboxOutputLimitException();
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static async Task DrainBoundedAsync(Stream stream, int maximumBytes)
    {
        var buffer = new byte[4096];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes) throw new SandboxOutputLimitException();
        }
    }

    private static async Task ObserveAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (SandboxOutputLimitException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class SandboxOutputLimitException : Exception;
}
