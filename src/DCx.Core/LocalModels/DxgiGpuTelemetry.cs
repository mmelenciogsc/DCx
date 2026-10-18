using System.Runtime.InteropServices;

namespace DCx.Core.LocalModels;

internal sealed record DxgiGpuMemorySnapshot(
    string Name,
    string Provider,
    long BudgetBytes,
    long AvailableBudgetBytes);

internal interface IDxgiGpuTelemetrySource
{
    IReadOnlyList<DxgiGpuMemorySnapshot> Read();
}

/// <summary>
/// Reads the current per-adapter local-memory budget exposed by Windows DXGI.
/// This is resource inventory, not compute-backend acceptance, a reservation,
/// or a peak-usage measurement.
/// </summary>
internal sealed class DxgiGpuTelemetrySource : IDxgiGpuTelemetrySource
{
    private const int MaximumAdapters = 16;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public IReadOnlyList<DxgiGpuMemorySnapshot> Read()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DxgiGpuMemorySnapshot>();

        IDXGIFactory1? factory = null;
        try
        {
            var factoryId = Factory1Id;
            var createResult = CreateDXGIFactory1(ref factoryId, out factory);
            if (createResult < 0 || factory is null) return Array.Empty<DxgiGpuMemorySnapshot>();

            var snapshots = new List<DxgiGpuMemorySnapshot>();
            for (uint index = 0; index < MaximumAdapters; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    var enumerateResult = factory.EnumAdapters1(index, out adapter);
                    if (enumerateResult == DxgiErrorNotFound) break;
                    if (enumerateResult < 0 || adapter is null) return Array.Empty<DxgiGpuMemorySnapshot>();

                    if (adapter.GetDesc1(out var description) < 0 ||
                        (description.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    var name = NormalizeName(description.Description);
                    if (name is null || adapter is not IDXGIAdapter3 adapter3) continue;
                    var queryResult = adapter3.QueryVideoMemoryInfo(
                            0,
                            DxgiMemorySegmentGroup.Local,
                            out var memory);
                    if (queryResult < 0 ||
                        memory.Budget is 0 or > long.MaxValue)
                    {
                        continue;
                    }

                    var available = memory.CurrentUsage >= memory.Budget
                        ? 0UL
                        : memory.Budget - memory.CurrentUsage;
                    if (available > long.MaxValue) continue;

                    snapshots.Add(new DxgiGpuMemorySnapshot(
                        name,
                        ProviderName(description.VendorId),
                        (long)memory.Budget,
                        (long)available));
                }
                finally
                {
                    ReleaseComObject(adapter);
                }
            }

            var unique = snapshots
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(unique);
        }
        catch (Exception exception) when (
            exception is COMException or
                InvalidCastException or
                TypeLoadException or
                EntryPointNotFoundException or
                DllNotFoundException or
                BadImageFormatException or
                SEHException)
        {
            return Array.Empty<DxgiGpuMemorySnapshot>();
        }
        finally
        {
            ReleaseComObject(factory);
        }
    }

    private static string? NormalizeName(string? value)
    {
        if (value is null) return null;
        var name = value.TrimEnd('\0').Trim();
        return name.Length is > 0 and <= 256 && !name.Any(char.IsControl) ? name : null;
    }

    private static string ProviderName(uint vendorId) => vendorId switch
    {
        0x1002 or 0x1022 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => "Windows DXGI",
    };

    private static void ReleaseComObject(object? value)
    {
        if (!OperatingSystem.IsWindows() || value is null || !Marshal.IsComObject(value)) return;
        try
        {
            _ = Marshal.ReleaseComObject(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidComObjectException or COMException)
        {
            // The runtime owns final RCW cleanup. Hardware inventory remains best effort.
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    private enum DxgiMemorySegmentGroup
    {
        Local = 0,
        NonLocal = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid riid, out nint parent);
        [PreserveSig] int EnumAdapters(uint adapter, out nint result);
        [PreserveSig] int MakeWindowAssociation(nint window, uint flags);
        [PreserveSig] int GetWindowAssociation(out nint window);
        [PreserveSig] int CreateSwapChain([MarshalAs(UnmanagedType.IUnknown)] object device, nint description, out nint swapChain);
        [PreserveSig] int CreateSoftwareAdapter(nint module, out nint adapter);

        [PreserveSig]
        int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? result);

        [PreserveSig]
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid riid, out nint parent);
        [PreserveSig] int EnumOutputs(uint output, out nint result);
        [PreserveSig] int GetDesc(out nint description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long userModeDriverVersion);

        [PreserveSig]
        int GetDesc1(out DxgiAdapterDescription1 description);
    }

    [ComImport]
    [Guid("645967a4-1392-4310-a798-8053ce3e93fd")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, nint data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, nint data);
        [PreserveSig] int GetParent(ref Guid riid, out nint parent);
        [PreserveSig] int EnumOutputs(uint output, out nint result);
        [PreserveSig] int GetDesc(out nint description);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long userModeDriverVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDescription1 description);
        [PreserveSig] int GetDesc2(out nint description);

        [PreserveSig]
        int RegisterHardwareContentProtectionTeardownStatusEvent(nint eventHandle, out uint cookie);

        void UnregisterHardwareContentProtectionTeardownStatus(uint cookie);

        [PreserveSig]
        int QueryVideoMemoryInfo(
            uint nodeIndex,
            DxgiMemorySegmentGroup memorySegmentGroup,
            out QueryVideoMemoryInfo memoryInfo);

        [PreserveSig]
        int SetVideoMemoryReservation(
            uint nodeIndex,
            DxgiMemorySegmentGroup memorySegmentGroup,
            ulong reservation);

        [PreserveSig]
        int RegisterVideoMemoryBudgetChangeNotificationEvent(nint eventHandle, out uint cookie);

        void UnregisterVideoMemoryBudgetChangeNotification(uint cookie);
    }
}
