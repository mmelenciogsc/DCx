using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace DCx.Core.LocalModels;

public sealed record LocalGpuProfile(
    string Name,
    string? Provider,
    string? DriverVersion,
    long? ReportedMemoryBytes,
    string? MemorySource);

public sealed record LocalHardwareProfile(
    DateTimeOffset CapturedUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    string CpuName,
    int LogicalProcessorCount,
    long PhysicalMemoryBytes,
    long AvailableMemoryBytes,
    long StorageTotalBytes,
    long StorageAvailableBytes,
    IReadOnlyList<LocalGpuProfile> Gpus);

public sealed record RawLocalHardwareProfile(
    string OperatingSystem,
    string ProcessArchitecture,
    string CpuName,
    int LogicalProcessorCount,
    long PhysicalMemoryBytes,
    long AvailableMemoryBytes,
    long StorageTotalBytes,
    long StorageAvailableBytes,
    IReadOnlyList<LocalGpuProfile> Gpus);

public interface ILocalHardwareProfileSource
{
    RawLocalHardwareProfile Read(string applicationDataPath);
}

public sealed class LocalHardwareProfiler
{
    private const long MaximumCapacityBytes = 1L << 60;
    private const long MaximumGpuMemoryBytes = 1L << 50;
    private readonly ILocalHardwareProfileSource source;
    private readonly TimeProvider timeProvider;

    public LocalHardwareProfiler(
        ILocalHardwareProfileSource? source = null,
        TimeProvider? timeProvider = null)
    {
        this.source = source ?? new WindowsLocalHardwareProfileSource();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LocalHardwareProfile Capture(string applicationDataPath)
    {
        if (string.IsNullOrWhiteSpace(applicationDataPath))
        {
            throw new ArgumentException("The application-data path is required.", nameof(applicationDataPath));
        }
        var canonicalPath = Path.GetFullPath(applicationDataPath);
        var raw = source.Read(canonicalPath)
            ?? throw new InvalidOperationException("The local hardware source returned no profile.");

        var operatingSystem = ValidateRequiredText(raw.OperatingSystem, 256, "operating system");
        var processArchitecture = ValidateRequiredText(raw.ProcessArchitecture, 32, "process architecture");
        var cpuName = ValidateRequiredText(raw.CpuName, 256, "processor name");
        if (raw.LogicalProcessorCount is < 1 or > 4096)
        {
            throw new InvalidOperationException("The logical processor count is invalid.");
        }
        ValidateCapacity(raw.PhysicalMemoryBytes, "physical memory");
        ValidateCapacity(raw.AvailableMemoryBytes, "available memory");
        if (raw.AvailableMemoryBytes > raw.PhysicalMemoryBytes)
        {
            throw new InvalidOperationException("Available memory exceeds physical memory.");
        }
        ValidateCapacity(raw.StorageTotalBytes, "storage capacity");
        ValidateCapacity(raw.StorageAvailableBytes, "available storage");
        if (raw.StorageAvailableBytes > raw.StorageTotalBytes)
        {
            throw new InvalidOperationException("Available storage exceeds storage capacity.");
        }
        if (raw.Gpus is null || raw.Gpus.Count > 16)
        {
            throw new InvalidOperationException("The graphics-adapter inventory is invalid.");
        }

        var gpus = new List<LocalGpuProfile>(raw.Gpus.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawGpu in raw.Gpus)
        {
            if (rawGpu is null)
            {
                throw new InvalidOperationException("The graphics-adapter inventory contains an empty item.");
            }
            var gpu = new LocalGpuProfile(
                ValidateRequiredText(rawGpu.Name, 256, "graphics-adapter name"),
                ValidateOptionalText(rawGpu.Provider, 128, "graphics provider"),
                ValidateOptionalText(rawGpu.DriverVersion, 64, "graphics driver version"),
                ValidateGpuMemory(rawGpu.ReportedMemoryBytes),
                ValidateOptionalText(rawGpu.MemorySource, 64, "graphics memory source"));
            if ((gpu.ReportedMemoryBytes is null) != (gpu.MemorySource is null))
            {
                throw new InvalidOperationException("Graphics memory and its source must be reported together.");
            }
            var identity = string.Join('\n',
                gpu.Name,
                gpu.Provider ?? string.Empty,
                gpu.DriverVersion ?? string.Empty,
                gpu.ReportedMemoryBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                gpu.MemorySource ?? string.Empty);
            if (identities.Add(identity)) gpus.Add(gpu);
        }

        var orderedGpus = gpus
            .OrderByDescending(gpu => gpu.ReportedMemoryBytes ?? -1)
            .ThenBy(gpu => gpu.Name, StringComparer.Ordinal)
            .ToArray();
        return new LocalHardwareProfile(
            timeProvider.GetUtcNow(),
            operatingSystem,
            processArchitecture,
            cpuName,
            raw.LogicalProcessorCount,
            raw.PhysicalMemoryBytes,
            raw.AvailableMemoryBytes,
            raw.StorageTotalBytes,
            raw.StorageAvailableBytes,
            Array.AsReadOnly(orderedGpus));
    }

    private static void ValidateCapacity(long value, string label)
    {
        if (value <= 0 || value > MaximumCapacityBytes)
        {
            throw new InvalidOperationException($"The {label} value is invalid.");
        }
    }

    private static long? ValidateGpuMemory(long? value)
    {
        if (value is null) return null;
        if (value <= 0 || value > MaximumGpuMemoryBytes)
        {
            throw new InvalidOperationException("The graphics memory value is invalid.");
        }
        return value;
    }

    private static string ValidateRequiredText(string? value, int maximumLength, string label) =>
        ValidateOptionalText(value, maximumLength, label)
        ?? throw new InvalidOperationException($"The {label} is unavailable.");

    private static string? ValidateOptionalText(string? value, int maximumLength, string label)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"The {label} is invalid.");
        }
        return value;
    }
}

public sealed class WindowsLocalHardwareProfileSource : ILocalHardwareProfileSource
{
    private const string VideoRegistryPath = @"SYSTEM\CurrentControlSet\Control\Video";
    private const string CpuRegistryPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public RawLocalHardwareProfile Read(string applicationDataPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native hardware inventory requires Windows.");
        }

        try
        {
            var memory = ReadMemoryStatus();
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(applicationDataPath));
            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                throw new InvalidOperationException("The application-data storage volume is unavailable.");
            }
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady)
            {
                throw new InvalidOperationException("The application-data storage volume is not ready.");
            }

            using var cpuKey = Registry.LocalMachine.OpenSubKey(CpuRegistryPath, writable: false);
            var cpuName = ReadRegistryText(cpuKey?.GetValue("ProcessorNameString"))
                ?? throw new InvalidOperationException("The processor identity is unavailable.");

            return new RawLocalHardwareProfile(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                cpuName,
                Environment.ProcessorCount,
                CheckedLong(memory.TotalPhysical, "physical memory"),
                CheckedLong(memory.AvailablePhysical, "available memory"),
                drive.TotalSize,
                drive.AvailableFreeSpace,
                Array.AsReadOnly(ReadGpus().ToArray()));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException("The native Windows hardware inventory could not be read safely.", exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<LocalGpuProfile> ReadGpus()
    {
        using var videoRoot = Registry.LocalMachine.OpenSubKey(VideoRegistryPath, writable: false);
        if (videoRoot is null) yield break;
        var yielded = 0;
        foreach (var adapterKeyName in videoRoot.GetSubKeyNames()
                     .OrderBy(value => value, StringComparer.Ordinal)
                     .Take(64))
        {
            using var adapterKey = videoRoot.OpenSubKey($@"{adapterKeyName}\0000", writable: false);
            if (adapterKey is null) continue;
            var name = ReadRegistryText(adapterKey.GetValue("DriverDesc")) ??
                       ReadRegistryText(adapterKey.GetValue("HardwareInformation.AdapterString"));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var memory = ReadRegistryMemory(adapterKey.GetValue("HardwareInformation.qwMemorySize"));
            var memorySource = memory is null ? null : "HardwareInformation.qwMemorySize";
            if (memory is null)
            {
                memory = ReadRegistryMemory(adapterKey.GetValue("HardwareInformation.MemorySize"));
                memorySource = memory is null ? null : "HardwareInformation.MemorySize";
            }
            yield return new LocalGpuProfile(
                name.Trim(),
                ReadRegistryText(adapterKey.GetValue("ProviderName"))?.Trim(),
                ReadRegistryText(adapterKey.GetValue("DriverVersion"))?.Trim(),
                memory,
                memorySource);
            yielded++;
            if (yielded == 16) yield break;
        }
    }

    private static string? ReadRegistryText(object? value)
    {
        if (value is string text) return text.TrimEnd('\0');
        if (value is string[] values) return values.FirstOrDefault()?.TrimEnd('\0');
        if (value is not byte[] bytes || bytes.Length is < 2 or > 1024 || bytes.Length % 2 != 0) return null;
        return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static long? ReadRegistryMemory(object? value)
    {
        ulong unsigned = value switch
        {
            int intValue => unchecked((uint)intValue),
            uint uintValue => uintValue,
            long longValue when longValue >= 0 => (ulong)longValue,
            ulong ulongValue => ulongValue,
            byte[] { Length: 4 } bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            byte[] { Length: 8 } bytes => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => 0,
        };
        return unsigned is 0 or > long.MaxValue ? null : (long)unsigned;
    }

    private static MemoryStatus ReadMemoryStatus()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("Windows did not return physical-memory status.");
        }
        return status;
    }

    private static long CheckedLong(ulong value, string label)
    {
        if (value is 0 or > long.MaxValue)
        {
            throw new InvalidOperationException($"The Windows {label} value is invalid.");
        }
        return (long)value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
