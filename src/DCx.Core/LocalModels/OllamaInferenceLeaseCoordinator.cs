using System.Collections.Concurrent;
using System.Diagnostics;

namespace DCx.Core.LocalModels;

internal interface IOllamaInferenceLeaseCoordinator
{
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken);
}

internal sealed class OllamaInferenceLeaseCoordinator : IOllamaInferenceLeaseCoordinator
{
    internal const string LockFileName = ".dcx-ollama-inference.lock";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string coordinationRoot;
    private readonly TimeSpan maximumWait;
    private readonly TimeSpan retryDelay;

    internal OllamaInferenceLeaseCoordinator(
        string coordinationRoot,
        TimeSpan? maximumWait = null,
        TimeSpan? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationRoot);
        this.coordinationRoot = Path.GetFullPath(coordinationRoot);
        this.maximumWait = maximumWait ?? TimeSpan.FromMinutes(5);
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        if (this.maximumWait < TimeSpan.FromMilliseconds(100) ||
            this.maximumWait > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWait),
                "The Ollama inference wait must be between 100 milliseconds and five minutes.");
        }
        if (this.retryDelay < TimeSpan.FromMilliseconds(10) ||
            this.retryDelay > TimeSpan.FromSeconds(1) ||
            this.retryDelay >= this.maximumWait)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelay),
                "The Ollama inference retry delay is outside its safety bound.");
        }
    }

    internal static OllamaInferenceLeaseCoordinator CreateForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            throw new InvalidOperationException(
                "The current-user local application-data directory is unavailable for Ollama coordination.");
        }

        return new OllamaInferenceLeaseCoordinator(Path.Combine(
            localApplicationData,
            "DCx",
            "LocalModels",
            "OllamaCoordination"));
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.StartNew();
        var root = LocalModelStoragePathGuard.EnsureDirectory(
            coordinationRoot,
            "Ollama inference coordination");
        var localGate = LocalGates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        if (!await localGate.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false))
        {
            throw Busy();
        }

        try
        {
            var lockPath = Path.Combine(root, LockFileName);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (started.Elapsed >= maximumWait) throw Busy();

                var validatedPath = LocalModelStoragePathGuard.ValidateFile(
                    lockPath,
                    mustExist: false,
                    "Ollama inference coordination lock");
                try
                {
                    var stream = new FileStream(
                        validatedPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough);
                    try
                    {
                        _ = LocalModelStoragePathGuard.ValidateFile(
                            validatedPath,
                            mustExist: true,
                            "Ollama inference coordination lock");
                        if (stream.Length != 0)
                        {
                            throw new InvalidOperationException(
                                "The Ollama inference coordination lock contains unexpected data.");
                        }
                        return new Lease(stream, localGate);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException) when (started.Elapsed < maximumWait)
                {
                    var remaining = maximumWait - started.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw Busy();
                    await Task.Delay(
                        remaining < retryDelay ? remaining : retryDelay,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw Busy();
                }
            }
        }
        catch
        {
            localGate.Release();
            throw;
        }
    }

    private static InvalidOperationException Busy() => new(
        "Another DCx process is using the local Ollama inference runtime. Wait for it to finish, then try again.");

    private sealed class Lease(FileStream stream, SemaphoreSlim localGate) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stream.Dispose();
            localGate.Release();
        }
    }
}

internal sealed class UncoordinatedOllamaInferenceLease : IOllamaInferenceLeaseCoordinator
{
    internal static UncoordinatedOllamaInferenceLease Instance { get; } = new();

    private UncoordinatedOllamaInferenceLease()
    {
    }

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IDisposable>(NoopLease.Instance);
    }

    private sealed class NoopLease : IDisposable
    {
        internal static NoopLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
