using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using DCx.Core.Broker;

namespace DCx.Desktop.Broker;

public sealed class WindowsProcessBroker
{
    private const int MaximumCapturedCharacters = 32_768;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<int, Process> _launchedProcesses = new();
    private readonly ProcessBrokerPolicy _policy;
    private readonly string _repositoryRoot;

    public WindowsProcessBroker(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _policy = new ProcessBrokerPolicy(_repositoryRoot);
    }

    public bool IsTrackedProcess(int processId) => _launchedProcesses.ContainsKey(processId);

    public async Task<BrokerExecutionResult> ExecuteAsync(
        BrokerRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorization = _policy.Evaluate(request);
        if (!authorization.IsAllowed)
        {
            return new BrokerExecutionResult(false, null, null, string.Empty, string.Empty, authorization.Reason);
        }

        return request.Operation switch
        {
            BrokerOperation.LaunchControlledFixture => LaunchInteractive(
                Path.Combine(
                    _repositoryRoot,
                    "tests",
                    "DCx.NativeFixture",
                    "bin",
                    "Release",
                    "net10.0-windows10.0.19041.0",
                    "DCx.NativeFixture.exe"),
                []),
            BrokerOperation.LaunchNotepad => LaunchInteractive(
                Path.Combine(Environment.SystemDirectory, "notepad.exe"),
                [authorization.CanonicalTargetPath!]),
            BrokerOperation.LaunchCalculator => LaunchInteractive(
                Path.Combine(Environment.SystemDirectory, "calc.exe"),
                []),
            BrokerOperation.InspectPathReadOnly => await RunReadOnlyPowerShellAsync(
                authorization.CanonicalTargetPath!,
                cancellationToken),
            _ => new BrokerExecutionResult(false, null, null, string.Empty, string.Empty, "Unsupported broker operation."),
        };
    }

    public bool TryGetTrackedProcess(int processId, out Process? process)
    {
        var found = _launchedProcesses.TryGetValue(processId, out var tracked);
        process = tracked;
        return found;
    }

    public bool CloseTrackedWindow(int processId)
    {
        if (!_launchedProcesses.TryGetValue(processId, out var process) || process.HasExited)
        {
            return false;
        }

        return process.CloseMainWindow();
    }

    private BrokerExecutionResult LaunchInteractive(string executablePath, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(executablePath))
        {
            return new BrokerExecutionResult(false, null, null, string.Empty, string.Empty, "The allowlisted executable is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = _repositoryRoot,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return new BrokerExecutionResult(false, null, null, string.Empty, string.Empty, "Windows did not start the allowlisted process.");
        }

        _launchedProcesses[process.Id] = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _launchedProcesses.TryRemove(process.Id, out _);
        return new BrokerExecutionResult(true, process.Id, null, string.Empty, string.Empty, "Allowlisted process started without elevation.");
    }

    private async Task<BrokerExecutionResult> RunReadOnlyPowerShellAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(_repositoryRoot, "scripts", "broker", "read-only.ps1");
        if (!File.Exists(scriptPath))
        {
            return new BrokerExecutionResult(false, null, null, string.Empty, string.Empty, "The signed-in-repository broker script is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo",
                     "-NoProfile",
                     "-NonInteractive",
                     "-ExecutionPolicy",
                     "RemoteSigned",
                     "-File",
                     scriptPath,
                     "-Operation",
                     "InspectPath",
                     "-TargetPath",
                     targetPath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new BrokerExecutionResult(false, process.Id, null, string.Empty, string.Empty, "The read-only command exceeded its time limit.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return new BrokerExecutionResult(false, process.Id, null, string.Empty, string.Empty, "The read-only command was cancelled.");
        }

        var standardOutput = Limit(await standardOutputTask);
        var standardError = Limit(await standardErrorTask);
        return new BrokerExecutionResult(
            process.ExitCode == 0,
            process.Id,
            process.ExitCode,
            standardOutput,
            standardError,
            process.ExitCode == 0 ? "Read-only inspection completed." : "Read-only inspection failed.");
    }

    private static string Limit(string value) =>
        value.Length <= MaximumCapturedCharacters ? value : value[..MaximumCapturedCharacters];
}
