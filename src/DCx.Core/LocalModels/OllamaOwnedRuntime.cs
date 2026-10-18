using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DCx.Core.LocalModels;

public sealed record OllamaOwnedRuntimeOptions(
    string ModelRoot,
    TimeSpan StartupTimeout,
    int? Port = null);

public sealed class OllamaOwnedRuntime : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stdoutDrain;
    private readonly Task stderrDrain;
    private readonly IReadOnlyList<FileStream> runtimeLeases;
    private readonly SafeFileHandle jobHandle;
    private readonly string operationRoot;
    private int disposed;

    internal OllamaOwnedRuntime(
        Process process,
        Task stdoutDrain,
        Task stderrDrain,
        IReadOnlyList<FileStream> runtimeLeases,
        SafeFileHandle jobHandle,
        string operationRoot,
        Uri baseAddress,
        string version)
    {
        this.process = process;
        this.stdoutDrain = stdoutDrain;
        this.stderrDrain = stderrDrain;
        this.runtimeLeases = runtimeLeases;
        this.jobHandle = jobHandle;
        this.operationRoot = operationRoot;
        BaseAddress = baseAddress;
        Version = version;
        ProcessId = process.Id;
    }

    public Uri BaseAddress { get; }
    public string Version { get; }
    public int ProcessId { get; }

    public bool IsRunning =>
        Volatile.Read(ref disposed) == 0 && !process.HasExited;

    public OllamaLocalClient CreateClient(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (process.HasExited) throw new InvalidOperationException("The DCx-owned Ollama runtime is no longer running.");
        return new OllamaLocalClient(BaseAddress, timeout);
    }

    internal OllamaLocalClient CreateRetainedModelClient(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (process.HasExited) throw new InvalidOperationException("The DCx-owned Ollama runtime is no longer running.");
        return new OllamaLocalClient(
            OllamaLocalClient.CreateHardenedHandler(),
            BaseAddress,
            timeout,
            OllamaInferenceLeaseCoordinator.CreateForCurrentUser(),
            WindowsLocalModelResourceAdmission.CreateForCurrentUser(),
            retainPinnedModel: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            jobHandle.Dispose();
            foreach (var lease in runtimeLeases) lease.Dispose();
            TryRemoveOperationRoot(operationRoot);
        }
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static void TryRemoveOperationRoot(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class OllamaOwnedRuntimeLauncher
{
    private const int MaximumVersionCharacters = 128;
    private readonly IOllamaTcpListenerOwnerProbe ownerProbe;

    public OllamaOwnedRuntimeLauncher() : this(new WindowsOllamaTcpListenerOwnerProbe())
    {
    }

    internal OllamaOwnedRuntimeLauncher(IOllamaTcpListenerOwnerProbe ownerProbe) =>
        this.ownerProbe = ownerProbe ?? throw new ArgumentNullException(nameof(ownerProbe));

    public async Task<OllamaOwnedRuntime> StartAsync(
        ApprovedOllamaRuntimeBinding binding,
        OllamaOwnedRuntimeOptions options,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The DCx-owned Ollama runtime is available only on Windows.");
        if (!explicitlyApproved)
            throw new InvalidOperationException("Starting the reviewed Ollama runtime requires explicit approval.");
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);
        OllamaRuntimeReviewer.ValidateBinding(binding);
        if (options.StartupTimeout < TimeSpan.FromSeconds(1) || options.StartupTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options), "Ollama startup must be bounded between one second and two minutes.");
        var modelRoot = OllamaRuntimeSelectionStore.ValidateModelRoot(options.ModelRoot);
        var port = options.Port ?? ReserveEphemeralPort();
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "The Ollama loopback port is invalid.");

        var leases = await VerifyAndLeaseRuntimeAsync(binding, cancellationToken).ConfigureAwait(false);
        var operationRoot = Path.Combine(Path.GetTempPath(), "DCx", "ollama-owned", Guid.NewGuid().ToString("N"));
        Process? process = null;
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        SafeFileHandle? jobHandle = null;
        try
        {
            Directory.CreateDirectory(operationRoot);
            if ((File.GetAttributes(operationRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The private Ollama runtime directory is unsafe.");
            var startInfo = CreateStartInfo(
                binding.ExecutablePath,
                binding.RuntimeRoot,
                modelRoot,
                operationRoot,
                port,
                binding.RuntimeProfile);
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The reviewed Ollama runtime could not start.");
            try { jobHandle = WindowsKillOnCloseJob.CreateAndAssign(process); }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "The reviewed Ollama runtime could not be contained in its owned process job.",
                    exception);
            }
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            var baseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
            var version = await WaitForReadyAsync(
                process,
                baseAddress,
                port,
                options.StartupTimeout,
                cancellationToken).ConfigureAwait(false);
            var result = new OllamaOwnedRuntime(
                process,
                stdout,
                stderr,
                leases,
                jobHandle,
                operationRoot,
                baseAddress,
                version);
            process = null;
            jobHandle = null;
            leases = [];
            return result;
        }
        catch
        {
            if (process is not null)
            {
                OllamaOwnedRuntime.Kill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                process.Dispose();
            }
            foreach (var lease in leases) lease.Dispose();
            jobHandle?.Dispose();
            OllamaOwnedRuntime.TryRemoveOperationRoot(operationRoot);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string runtimeRoot,
        string modelRoot,
        string operationRoot,
        int port,
        string runtimeProfile = OllamaRuntimeReviewer.CompleteTreeProfile)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = runtimeRoot,
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "WINDIR");
        CopyEnvironment(startInfo, "SystemDrive");
        startInfo.Environment["TEMP"] = operationRoot;
        startInfo.Environment["TMP"] = operationRoot;
        startInfo.Environment["USERPROFILE"] = operationRoot;
        startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
        startInfo.Environment["OLLAMA_MODELS"] = modelRoot;
        startInfo.Environment["OLLAMA_NO_CLOUD"] = "1";
        startInfo.Environment["OLLAMA_NOPRUNE"] = "1";
        startInfo.Environment["OLLAMA_KEEP_ALIVE"] = "0s";
        startInfo.Environment["OLLAMA_MAX_LOADED_MODELS"] = "1";
        startInfo.Environment["OLLAMA_NUM_PARALLEL"] = "1";
        startInfo.Environment["OLLAMA_MAX_QUEUE"] = "1";
        startInfo.Environment["OLLAMA_MAX_TRANSFER_STREAMS"] = "1";
        startInfo.Environment["OLLAMA_ORIGINS"] = "http://127.0.0.1";
        startInfo.Environment["OLLAMA_VULKAN"] = "0";
        if (runtimeProfile == OllamaRuntimeReviewer.NvidiaCudaV13Profile)
            startInfo.Environment["OLLAMA_LLM_LIBRARY"] = "cuda_v13";
        return startInfo;
    }

    private async Task<string> WaitForReadyAsync(
        Process process,
        Uri baseAddress,
        int port,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        using var handler = OllamaLocalClient.CreateHardenedHandler();
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(1),
        };
        using var timeout = new CancellationTokenSource(startupTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The reviewed Ollama runtime exited before readiness.");
                try
                {
                    using var response = await client.GetAsync("api/version", deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && ownerProbe.IsOwnedLoopbackListener(port, process.Id))
                    {
                        var bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            !document.RootElement.TryGetProperty("version", out var versionElement) ||
                            versionElement.ValueKind != JsonValueKind.String ||
                            document.RootElement.EnumerateObject().Count() != 1)
                            throw new InvalidOperationException("The reviewed Ollama runtime returned invalid version evidence.");
                        var version = versionElement.GetString();
                        if (string.IsNullOrWhiteSpace(version) || version.Length > MaximumVersionCharacters ||
                            version != version.Trim() || version.Any(char.IsControl))
                            throw new InvalidOperationException("The reviewed Ollama runtime version is invalid.");
                        return version;
                    }
                }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }
                catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The reviewed Ollama runtime did not become ready within its startup limit.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 4096;
        if (content.Headers.ContentLength is < 0 or > maximumBytes)
            throw new InvalidOperationException("The reviewed Ollama runtime version response exceeds its byte limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(maximumBytes);
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException("The reviewed Ollama runtime version response exceeds its byte limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task<IReadOnlyList<FileStream>> VerifyAndLeaseRuntimeAsync(
        ApprovedOllamaRuntimeBinding binding,
        CancellationToken cancellationToken)
    {
        var actualPaths = OllamaRuntimeReviewer.EnumerateRuntimeFiles(
            binding.RuntimeRoot,
            binding.RuntimeProfile);
        var expectedPaths = binding.Files.Select(file => file.RelativePath).ToArray();
        var actualRelative = actualPaths.Select(path =>
            Path.GetRelativePath(binding.RuntimeRoot, path).Replace('\\', '/')).ToArray();
        if (!actualRelative.SequenceEqual(expectedPaths, StringComparer.Ordinal))
            throw new InvalidOperationException("The reviewed Ollama runtime file inventory changed before launch.");

        var leases = new List<FileStream>(binding.Files.Count);
        try
        {
            for (var index = 0; index < binding.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = binding.Files[index];
                var path = OllamaRuntimeReviewer.ResolveContained(binding.RuntimeRoot, expected.RelativePath);
                var lease = OllamaRuntimeReviewer.OpenReadLease(path);
                leases.Add(lease);
                if (lease.Length != expected.Length)
                    throw new InvalidOperationException("A reviewed Ollama runtime file length changed before launch.");
                var digest = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("A reviewed Ollama runtime file failed SHA-256 verification.");
                lease.Position = 0;
            }
            actualPaths = OllamaRuntimeReviewer.EnumerateRuntimeFiles(
                binding.RuntimeRoot,
                binding.RuntimeProfile);
            actualRelative = actualPaths.Select(path =>
                Path.GetRelativePath(binding.RuntimeRoot, path).Replace('\\', '/')).ToArray();
            if (!actualRelative.SequenceEqual(expectedPaths, StringComparer.Ordinal))
                throw new InvalidOperationException("The reviewed Ollama runtime file inventory changed while it was leased.");
            return leases.AsReadOnly();
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(1);
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener.Stop(); }
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) != 0) { }
    }
}

internal static class WindowsKillOnCloseJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    internal static SafeFileHandle CreateAndAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "DCx could not create the owned Ollama process job.");
        }
        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "DCx could not configure the owned Ollama process job.");
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "DCx could not assign Ollama to its owned process job.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}

internal interface IOllamaTcpListenerOwnerProbe
{
    bool IsOwnedLoopbackListener(int port, int processId);
}

internal sealed class WindowsOllamaTcpListenerOwnerProbe : IOllamaTcpListenerOwnerProbe
{
    public bool IsOwnedLoopbackListener(int port, int processId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            if (listener.Address.Equals(IPAddress.Loopback) && listener.Port == port)
                return GetOwnerProcessId(port) == processId;
        }
        return false;
    }

    private static int? GetOwnerProcessId(int port)
    {
        uint size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
        if (first != ErrorInsufficientBuffer || size is 0 or > 16 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0) != 0)
                return null;
            var count = Marshal.ReadInt32(buffer);
            if (count is < 0 or > 65_536) return null;
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var offset = sizeof(uint);
            for (var index = 0; index < count; index++, offset += rowSize)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + offset);
                var localPort = BinaryPrimitives.ReverseEndianness((ushort)(row.LocalPort & 0xffff));
                if (row.LocalAddress == LoopbackAddress && localPort == port)
                    return checked((int)row.OwningProcessId);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const uint LoopbackAddress = 0x0100007f;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
