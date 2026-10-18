using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class OllamaInferenceLeaseCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-ollama-lease-{Guid.NewGuid():N}");

    [Fact]
    public async Task SameUserLease_SerializesCancellationAndRecovery()
    {
        var first = new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10));
        var second = new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(10));

        using (await first.AcquireAsync(CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                second.AcquireAsync(cancellation.Token));
        }

        using (await second.AcquireAsync(CancellationToken.None))
        {
            var lockPath = Path.Combine(root, OllamaInferenceLeaseCoordinator.LockFileName);
            Assert.True(File.Exists(lockPath));
            Assert.Equal(0, new FileInfo(lockPath).Length);
            Assert.Throws<IOException>(() => new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None));
        }

        using var recovered = await first.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExternalContention_TimesOutWithStableMessageThenRecovers()
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, OllamaInferenceLeaseCoordinator.LockFileName);
        await using (var external = new FileStream(
                         lockPath,
                         FileMode.OpenOrCreate,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            var coordinator = new OllamaInferenceLeaseCoordinator(
                root,
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(10));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.AcquireAsync(CancellationToken.None));
            Assert.Equal(
                "Another DCx process is using the local Ollama inference runtime. Wait for it to finish, then try again.",
                error.Message);
        }

        var recoveredCoordinator = new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));
        using var recovered = await recoveredCoordinator.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnsafeLockPath_IsDeniedWithoutFollowingIt()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, OllamaInferenceLeaseCoordinator.LockFileName));
        var coordinator = new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireAsync(CancellationToken.None));

        Assert.Contains("path is a directory", error.Message, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(Path.Combine(root, OllamaInferenceLeaseCoordinator.LockFileName));
        var lockPath = Path.Combine(root, OllamaInferenceLeaseCoordinator.LockFileName);
        await File.WriteAllTextAsync(lockPath, "untrusted lock content");

        error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireAsync(CancellationToken.None));

        Assert.Equal("The Ollama inference coordination lock contains unexpected data.", error.Message);
        Assert.Equal("untrusted lock content", await File.ReadAllTextAsync(lockPath));
    }

    [Fact]
    public async Task PreCanceledLease_CreatesNoStorageAndTimingBoundsFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromMilliseconds(99),
            TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));

        var coordinator = new OllamaInferenceLeaseCoordinator(
            root,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.AcquireAsync(canceled.Token));

        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
