using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using DCx.Core.Deployment;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class OllamaOwnedRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-owned-ollama-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");

    [Fact]
    public async Task Review_BindsExactCompleteRuntimeTreeAndSignerEvidence()
    {
        var fixture = CopyFixtureRuntime();
        var review = await Reviewer().ReviewAsync(fixture.Executable, now);

        Assert.Equal(OllamaRuntimeReviewer.OfficialLicenseUri, review.OfficialLicenseUri);
        Assert.Equal("Valid", review.Binding.AuthenticodeStatus);
        Assert.Equal("CN=Fixture Ollama", review.Binding.SignerSubject);
        Assert.Equal(new string('a', 64), review.Binding.SignerCertificateSha256);
        Assert.Equal(now, review.Binding.ReviewedUtc);
        Assert.Equal(Path.GetFileName(fixture.Executable), review.Binding.ExecutableRelativePath);
        Assert.Equal(review.Binding.Files.Sum(file => file.Length), review.TotalBytes);
        Assert.Equal(
            OllamaRuntimeReviewer.ComputeTreeDigest(
                review.Binding.RuntimeProfile,
                review.Binding.ExecutableRelativePath,
                review.Binding.Files),
            review.Binding.TreeSha256);
        Assert.Contains(review.Binding.Files, file => file.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
        Assert.All(review.Binding.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public async Task Review_NvidiaProfileBindsOnlyForcedCudaV13DependencyScope()
    {
        var fixture = CopyFixtureRuntime();
        foreach (var relative in new[]
                 {
                     "lib/ollama/llama-server.exe",
                     "lib/ollama/cuda_v13/ggml-cuda.dll",
                     "lib/ollama/cuda_v12/ignored.dll",
                     "lib/ollama/rocm_v7_1/ignored.dll",
                     "lib/ollama/vulkan/ignored.dll",
                 })
        {
            var path = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, relative);
        }

        var review = await Reviewer().ReviewAsync(
            fixture.Executable,
            OllamaRuntimeReviewer.NvidiaCudaV13Profile,
            now);

        Assert.Equal(OllamaRuntimeReviewer.NvidiaCudaV13Profile, review.Binding.RuntimeProfile);
        Assert.Contains(review.Binding.Files, file => file.RelativePath == "lib/ollama/cuda_v13/ggml-cuda.dll");
        Assert.DoesNotContain(review.Binding.Files, file => file.RelativePath.Contains("cuda_v12", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Binding.Files, file => file.RelativePath.Contains("rocm", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Binding.Files, file => file.RelativePath.Contains("vulkan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_RejectsLinkedContentAndInconsistentSignerEvidence()
    {
        var fixture = CopyFixtureRuntime();
        var outside = Path.Combine(root, "outside.dll");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "linked.dll"), outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Reviewer().ReviewAsync(fixture.Executable, now));
        File.Delete(Path.Combine(fixture.Root, "linked.dll"));

        var invalid = new OllamaRuntimeReviewer(new SignatureProbe("NotSigned", "CN=Unexpected", new string('b', 64)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ReviewAsync(fixture.Executable, now));
    }

    [Fact]
    public async Task Binding_RejectsChangedTreeDigestDuplicatePathAndEscapingExecutable()
    {
        var review = await Reviewer().ReviewAsync(CopyFixtureRuntime().Executable, now);
        var files = review.Binding.Files.ToArray();

        Assert.Throws<InvalidOperationException>(() => OllamaRuntimeReviewer.ValidateBinding(
            review.Binding with { TreeSha256 = new string('0', 64) }));
        Assert.Throws<InvalidOperationException>(() => OllamaRuntimeReviewer.ValidateBinding(
            review.Binding with { Files = files.Append(files[0]).ToArray() }));
        Assert.Throws<InvalidOperationException>(() => OllamaRuntimeReviewer.ValidateBinding(
            review.Binding with { ExecutableRelativePath = "../outside.exe" }));
    }

    [Fact]
    public void StartInfo_ScrubsAmbientStateAndPinsNoCloudSingleWorkloadPolicy()
    {
        var info = OllamaOwnedRuntimeLauncher.CreateStartInfo(
            @"C:\runtime\ollama.exe",
            @"C:\runtime",
            @"D:\models",
            @"C:\temp\operation",
            23456);

        Assert.Equal(["serve"], info.ArgumentList);
        Assert.Equal("127.0.0.1:23456", info.Environment["OLLAMA_HOST"]);
        Assert.Equal(@"D:\models", info.Environment["OLLAMA_MODELS"]);
        Assert.Equal("1", info.Environment["OLLAMA_NO_CLOUD"]);
        Assert.Equal("1", info.Environment["OLLAMA_NOPRUNE"]);
        Assert.Equal("0s", info.Environment["OLLAMA_KEEP_ALIVE"]);
        Assert.Equal("1", info.Environment["OLLAMA_MAX_LOADED_MODELS"]);
        Assert.Equal("1", info.Environment["OLLAMA_NUM_PARALLEL"]);
        Assert.Equal("1", info.Environment["OLLAMA_MAX_QUEUE"]);
        Assert.Equal("1", info.Environment["OLLAMA_MAX_TRANSFER_STREAMS"]);
        Assert.Equal("http://127.0.0.1", info.Environment["OLLAMA_ORIGINS"]);
        Assert.Equal("0", info.Environment["OLLAMA_VULKAN"]);
        Assert.Equal(@"C:\temp\operation", info.Environment["USERPROFILE"]);
        Assert.False(info.Environment.ContainsKey("DCX_FORBIDDEN_AMBIENT_SECRET"));
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.False(info.UseShellExecute);

        var cudaInfo = OllamaOwnedRuntimeLauncher.CreateStartInfo(
            @"C:\runtime\ollama.exe",
            @"C:\runtime",
            @"D:\models",
            @"C:\temp\operation",
            23457,
            OllamaRuntimeReviewer.NvidiaCudaV13Profile);
        Assert.Equal("cuda_v13", cudaInfo.Environment["OLLAMA_LLM_LIBRARY"]);
        Assert.Equal("0", cudaInfo.Environment["OLLAMA_VULKAN"]);
    }

    [Fact]
    public async Task Runtime_RequiresApprovalStartsOwnedListenerAndStopsWithoutTouchingModelFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CopyFixtureRuntime();
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var sentinel = Path.Combine(modelRoot, "sentinel.bin");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        var review = await Reviewer().ReviewAsync(fixture.Executable, now);
        var launcher = new OllamaOwnedRuntimeLauncher();
        var options = new OllamaOwnedRuntimeOptions(modelRoot, TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.StartAsync(review.Binding, options, explicitlyApproved: false));

        var prior = Environment.GetEnvironmentVariable("DCX_FORBIDDEN_AMBIENT_SECRET");
        Environment.SetEnvironmentVariable("DCX_FORBIDDEN_AMBIENT_SECRET", "must-not-cross-boundary");
        Uri address;
        int processId;
        try
        {
            await using (var runtime = await launcher.StartAsync(review.Binding, options, explicitlyApproved: true))
            {
                address = runtime.BaseAddress;
                processId = runtime.ProcessId;
                Assert.Equal("fixture-secure", runtime.Version);
                Assert.True(address.IsLoopback);
                Assert.NotEqual(11434, address.Port);
                using var client = runtime.CreateClient(TimeSpan.FromSeconds(5));
                Assert.Empty(await client.ListModelsAsync());
                Assert.Throws<IOException>(() => File.Open(
                    fixture.Executable,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DCX_FORBIDDEN_AMBIENT_SECRET", prior);
        }

        Assert.False(IsProcessAlive(processId));
        await AssertPortClosedAsync(address.Port);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Runtime_DeniesChangedOrExpandedTreeBeforeStartingChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CopyFixtureRuntime();
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var review = await Reviewer().ReviewAsync(fixture.Executable, now);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "DCx.FakeOllamaServer.dll"), "changed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new OllamaOwnedRuntimeLauncher().StartAsync(
            review.Binding,
            new(modelRoot, TimeSpan.FromSeconds(2)),
            explicitlyApproved: true));

        fixture = CopyFixtureRuntime("expanded");
        review = await Reviewer().ReviewAsync(fixture.Executable, now);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "injected.dll"), "injected");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OllamaOwnedRuntimeLauncher().StartAsync(
            review.Binding,
            new(modelRoot, TimeSpan.FromSeconds(2)),
            explicitlyApproved: true));
    }

    [Fact]
    public async Task Runtime_DeniesListenerWhoseOwnerCannotBeProvenAndKillsChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CopyFixtureRuntime();
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var review = await Reviewer().ReviewAsync(fixture.Executable, now);

        await Assert.ThrowsAsync<TimeoutException>(() => new OllamaOwnedRuntimeLauncher(new RejectingOwnerProbe()).StartAsync(
            review.Binding,
            new(modelRoot, TimeSpan.FromSeconds(1)),
            explicitlyApproved: true));
    }

    [Fact]
    public async Task Runtime_DeniesChildExitBeforeReadiness()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CopyFixtureRuntime("exit-fixture", "fixture-exit.exe");
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var review = await Reviewer().ReviewAsync(fixture.Executable, now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new OllamaOwnedRuntimeLauncher().StartAsync(
            review.Binding,
            new(modelRoot, TimeSpan.FromSeconds(2)),
            explicitlyApproved: true));
    }

    [Fact]
    public async Task JobObject_CloseKillsAssignedRuntimeProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = CopyFixtureRuntime();
        var modelRoot = Path.Combine(root, "models");
        var operationRoot = Path.Combine(root, "operation");
        Directory.CreateDirectory(modelRoot);
        Directory.CreateDirectory(operationRoot);
        var startInfo = OllamaOwnedRuntimeLauncher.CreateStartInfo(
            fixture.Executable,
            fixture.Root,
            modelRoot,
            operationRoot,
            ReservePort());
        using var process = System.Diagnostics.Process.Start(startInfo) ??
            throw new InvalidOperationException("The job fixture did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using (var job = WindowsKillOnCloseJob.CreateAndAssign(process))
        {
            Assert.False(process.HasExited);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr);
        Assert.True(process.HasExited);
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private OllamaRuntimeReviewer Reviewer() => new(
        new SignatureProbe("Valid", "CN=Fixture Ollama", new string('a', 64)));

    private Fixture CopyFixtureRuntime(string name = "runtime", string? executableName = null)
    {
        var sourceExecutable = FixtureExecutable();
        var source = Path.GetDirectoryName(sourceExecutable)!;
        var destination = Path.Combine(root, name);
        Directory.CreateDirectory(destination);
        foreach (var sourceFile in Directory.EnumerateFiles(source))
        {
            var targetName = Path.GetFileName(sourceFile);
            if (executableName is not null && string.Equals(sourceFile, sourceExecutable, StringComparison.OrdinalIgnoreCase))
                targetName = executableName;
            File.Copy(sourceFile, Path.Combine(destination, targetName), overwrite: false);
        }
        return new(destination, Path.Combine(destination, executableName ?? Path.GetFileName(sourceExecutable)));
    }

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(
            directory.FullName,
            "tests",
            "DCx.FakeOllamaServer",
            "bin",
            "Release",
            "net10.0",
            "DCx.FakeOllamaServer.exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException("Build the fixture first.", executable);
    }

    private static bool IsProcessAlive(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task AssertPortClosedAsync(int port)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
                    endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port))
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"The owned Ollama loopback listener on port {port} remained active after disposal.");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(1);
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener.Stop(); }
    }

    private sealed record Fixture(string Root, string Executable);

    private sealed class SignatureProbe(string status, string? subject, string? certificate) : IPackageAuthenticodeProbe
    {
        public Task<PackageAuthenticodeEvidence> InspectAsync(string packagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PackageAuthenticodeEvidence(status, subject, certificate));
        }
    }

    private sealed class RejectingOwnerProbe : IOllamaTcpListenerOwnerProbe
    {
        public bool IsOwnedLoopbackListener(int port, int processId) => false;
    }
}
