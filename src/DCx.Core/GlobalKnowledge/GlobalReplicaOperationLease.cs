using System.Collections.Concurrent;
using System.Diagnostics;

namespace DCx.Core.GlobalKnowledge;

internal sealed class GlobalReplicaOperationLease : IDisposable
{
    internal const string LockFileName = ".dcx-global-replica.lock";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);

    private readonly FileStream stream;
    private readonly SemaphoreSlim localGate;
    private bool disposed;

    private GlobalReplicaOperationLease(FileStream stream, SemaphoreSlim localGate)
    {
        this.stream = stream;
        this.localGate = localGate;
    }

    public static async Task<GlobalReplicaOperationLease> AcquireAsync(
        string replicaRoot,
        CancellationToken cancellationToken)
    {
        var canonicalRoot = GlobalStoragePathGuard.RequireDirectory(replicaRoot, "global replica root");
        var localGate = LocalGates.GetOrAdd(canonicalRoot, static _ => new SemaphoreSlim(1, 1));
        await localGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(canonicalRoot, LockFileName);
            var wait = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validatedPath = GlobalStoragePathGuard.ValidateFile(
                    lockPath,
                    mustExist: false,
                    "global replica operation lock");
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
                        _ = GlobalStoragePathGuard.ValidateFile(
                            validatedPath,
                            mustExist: true,
                            "global replica operation lock");
                        return new GlobalReplicaOperationLease(stream, localGate);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException) when (wait.Elapsed < MaximumWait)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw new GlobalReplicaException(
                        "Another DCx process is updating this global replica. Wait for it to finish, then try again.");
                }
            }
        }
        catch
        {
            localGate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
        localGate.Release();
    }
}
