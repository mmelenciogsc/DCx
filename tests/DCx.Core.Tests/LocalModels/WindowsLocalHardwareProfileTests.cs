using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class WindowsLocalHardwareProfileTests
{
    private static readonly DateTimeOffset CapturedUtc =
        new(2026, 10, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_ValidatesDeduplicatesAndOrdersBoundedHardwareEvidence()
    {
        var largeGpu = new LocalGpuProfile(
            "GPU Large",
            "Provider",
            "1.2.3.4",
            8L * 1024 * 1024 * 1024,
            "HardwareInformation.qwMemorySize",
            6L * 1024 * 1024 * 1024,
            "fixture live free memory");
        var profile = new LocalHardwareProfiler(
            new FixtureSource(new RawLocalHardwareProfile(
                "Microsoft Windows fixture",
                "X64",
                "Fixture CPU",
                12,
                16L * 1024 * 1024 * 1024,
                8L * 1024 * 1024 * 1024,
                1_000_000_000_000,
                600_000_000_000,
                [
                    new("GPU Small", null, null, null, null),
                    largeGpu,
                    largeGpu,
                ])),
            new FixedTimeProvider(CapturedUtc)).Capture(@"C:\fixture");

        Assert.Equal(CapturedUtc, profile.CapturedUtc);
        Assert.Equal(12, profile.LogicalProcessorCount);
        Assert.Equal(2, profile.Gpus.Count);
        Assert.Equal("GPU Large", profile.Gpus[0].Name);
        Assert.Equal(8L * 1024 * 1024 * 1024, profile.Gpus[0].ReportedMemoryBytes);
        Assert.Equal(6L * 1024 * 1024 * 1024, profile.Gpus[0].AvailableMemoryBytes);
        Assert.Equal("GPU Small", profile.Gpus[1].Name);
    }

    [Fact]
    public void Capture_RejectsMalformedCapacityTextAndGraphicsEvidence()
    {
        var valid = ValidRaw();
        Assert.Throws<InvalidOperationException>(() => Capture(valid with { AvailableMemoryBytes = valid.PhysicalMemoryBytes + 1 }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with { StorageAvailableBytes = valid.StorageTotalBytes + 1 }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with { CpuName = "bad\nname" }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with
        {
            Gpus = [new("GPU", null, null, 1024, null)],
        }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with
        {
            Gpus = [new("GPU", null, null, 1024, "fixture", 512, null)],
        }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with
        {
            Gpus = [new("GPU", null, null, null, null, 512, "fixture live")],
        }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with
        {
            Gpus = [new("GPU", null, null, 1024, "fixture", 2048, "fixture live")],
        }));
        Assert.Throws<InvalidOperationException>(() => Capture(valid with
        {
            Gpus = Enumerable.Range(0, 17)
                .Select(index => new LocalGpuProfile($"GPU {index}", null, null, null, null))
                .ToArray(),
        }));
    }

    [Fact]
    public void WindowsSource_CapturesCurrentHostWithoutAChildProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var profile = new LocalHardwareProfiler().Capture(Path.GetTempPath());

        Assert.Contains("Windows", profile.OperatingSystem, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(profile.LogicalProcessorCount, 1, 4096);
        Assert.True(profile.PhysicalMemoryBytes >= profile.AvailableMemoryBytes);
        Assert.True(profile.StorageTotalBytes >= profile.StorageAvailableBytes);
        Assert.InRange(profile.Gpus.Count, 0, 16);
        Assert.All(profile.Gpus, gpu => Assert.False(string.IsNullOrWhiteSpace(gpu.Name)));
        Assert.All(profile.Gpus.Where(gpu => gpu.AvailableMemoryBytes is not null), gpu =>
        {
            Assert.False(string.IsNullOrWhiteSpace(gpu.AvailableMemorySource));
            Assert.InRange(gpu.AvailableMemoryBytes!.Value, 0, gpu.ReportedMemoryBytes!.Value);
        });
    }

    [Fact]
    public void NvidiaTelemetry_LoadsOnlyInstalledSystemDriverAndReturnsBoundedLiveMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var libraryPath = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(libraryPath)) return;

        var snapshots = new NvidiaNvmlGpuTelemetrySource().Read();

        Assert.NotEmpty(snapshots);
        Assert.InRange(snapshots.Count, 1, 16);
        Assert.All(snapshots, snapshot =>
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Name));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.DriverVersion));
            Assert.InRange(snapshot.AvailableMemoryBytes, 0, snapshot.TotalMemoryBytes);
        });
    }

    private static LocalHardwareProfile Capture(RawLocalHardwareProfile raw) =>
        new LocalHardwareProfiler(new FixtureSource(raw), new FixedTimeProvider(CapturedUtc))
            .Capture(@"C:\fixture");

    private static RawLocalHardwareProfile ValidRaw() => new(
        "Microsoft Windows fixture",
        "X64",
        "Fixture CPU",
        8,
        16L * 1024 * 1024 * 1024,
        8L * 1024 * 1024 * 1024,
        1_000_000_000_000,
        600_000_000_000,
        [new("GPU", null, null, null, null)]);

    private sealed class FixtureSource(RawLocalHardwareProfile profile) : ILocalHardwareProfileSource
    {
        public RawLocalHardwareProfile Read(string applicationDataPath) => profile;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
