using System.Runtime.InteropServices;
using System.Text;

namespace DCx.Core.LocalModels;

internal sealed record NvidiaGpuMemorySnapshot(
    string Name,
    string DriverVersion,
    long TotalMemoryBytes,
    long AvailableMemoryBytes);

internal interface INvidiaGpuTelemetrySource
{
    IReadOnlyList<NvidiaGpuMemorySnapshot> Read();
}

internal sealed class NvidiaNvmlGpuTelemetrySource : INvidiaGpuTelemetrySource
{
    private const int Success = 0;
    private const int MaximumDevices = 16;
    private const int DeviceNameBufferBytes = 96;
    private const int DriverVersionBufferBytes = 80;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public IReadOnlyList<NvidiaGpuMemorySnapshot> Read()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<NvidiaGpuMemorySnapshot>();

        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory) || !Path.IsPathFullyQualified(systemDirectory))
        {
            return Array.Empty<NvidiaGpuMemorySnapshot>();
        }

        var systemDirectoryPath = Path.GetFullPath(systemDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var libraryPath = Path.GetFullPath(Path.Combine(systemDirectoryPath, "nvml.dll"));
        if (!string.Equals(Path.GetDirectoryName(libraryPath), systemDirectoryPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(libraryPath) ||
            !NativeLibrary.TryLoad(libraryPath, out var library))
        {
            return Array.Empty<NvidiaGpuMemorySnapshot>();
        }

        try
        {
            return ReadLoaded(library);
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or
                BadImageFormatException or
                SEHException or
                MarshalDirectiveException)
        {
            return Array.Empty<NvidiaGpuMemorySnapshot>();
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static IReadOnlyList<NvidiaGpuMemorySnapshot> ReadLoaded(nint library)
    {
        var initialize = GetExport<InitializeDelegate>(library, "nvmlInit_v2");
        var shutdown = GetExport<ShutdownDelegate>(library, "nvmlShutdown");
        var getCount = GetExport<GetDeviceCountDelegate>(library, "nvmlDeviceGetCount_v2");
        var getHandle = GetExport<GetDeviceHandleByIndexDelegate>(library, "nvmlDeviceGetHandleByIndex_v2");
        var getName = GetExport<GetDeviceNameDelegate>(library, "nvmlDeviceGetName");
        var getMemory = GetExport<GetDeviceMemoryInfoDelegate>(library, "nvmlDeviceGetMemoryInfo");
        var getDriver = GetExport<GetDriverVersionDelegate>(library, "nvmlSystemGetDriverVersion");

        if (initialize() != Success) return Array.Empty<NvidiaGpuMemorySnapshot>();
        try
        {
            if (getCount(out var count) != Success || count is 0 or > MaximumDevices)
            {
                return Array.Empty<NvidiaGpuMemorySnapshot>();
            }

            var driverBuffer = new byte[DriverVersionBufferBytes];
            if (getDriver(driverBuffer, (uint)driverBuffer.Length) != Success)
            {
                return Array.Empty<NvidiaGpuMemorySnapshot>();
            }
            var driverVersion = DecodeBounded(driverBuffer, "NVIDIA driver version", 64);

            var snapshots = new List<NvidiaGpuMemorySnapshot>((int)count);
            for (uint index = 0; index < count; index++)
            {
                if (getHandle(index, out var device) != Success || device == nint.Zero)
                {
                    return Array.Empty<NvidiaGpuMemorySnapshot>();
                }

                var nameBuffer = new byte[DeviceNameBufferBytes];
                if (getName(device, nameBuffer, (uint)nameBuffer.Length) != Success)
                {
                    return Array.Empty<NvidiaGpuMemorySnapshot>();
                }

                var memory = new NvmlMemory();
                if (getMemory(device, ref memory) != Success ||
                    memory.Total is 0 or > long.MaxValue ||
                    memory.Free > memory.Total ||
                    memory.Used > memory.Total ||
                    memory.Free > memory.Total - memory.Used)
                {
                    return Array.Empty<NvidiaGpuMemorySnapshot>();
                }

                snapshots.Add(new NvidiaGpuMemorySnapshot(
                    DecodeBounded(nameBuffer, "NVIDIA device name", 256),
                    driverVersion,
                    (long)memory.Total,
                    (long)memory.Free));
            }

            var unique = snapshots
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(unique);
        }
        finally
        {
            _ = shutdown();
        }
    }

    private static T GetExport<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out var address) || address == nint.Zero)
        {
            throw new EntryPointNotFoundException($"The NVIDIA telemetry export {name} is unavailable.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string DecodeBounded(byte[] buffer, string label, int maximumCharacters)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        if (terminator <= 0) throw new InvalidOperationException($"The {label} is unavailable.");
        var value = StrictUtf8.GetString(buffer, 0, terminator);
        if (value.Length > maximumCharacters ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"The {label} is invalid.");
        }
        return value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceCountDelegate(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceHandleByIndexDelegate(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceNameDelegate(nint device, [Out] byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceMemoryInfoDelegate(nint device, ref NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDriverVersionDelegate([Out] byte[] version, uint length);
}
