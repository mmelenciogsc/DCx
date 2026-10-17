using System.Text;

namespace DCx.Core.Reliability;

public sealed class ApplicationDataInUseException : InvalidOperationException
{
    public ApplicationDataInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ApplicationDataOperationLease : IDisposable
{
    public const string LockFileName = ".dcx-runtime.lock";

    private readonly FileStream stream;
    private bool disposed;

    private ApplicationDataOperationLease(string dataRoot, FileStream stream)
    {
        DataRoot = dataRoot;
        this.stream = stream;
    }

    public string DataRoot { get; }

    public static ApplicationDataOperationLease Acquire(string applicationDataRoot, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 64 || operation.Any(char.IsControl))
        {
            throw new ArgumentException("The application-data operation is invalid.", nameof(operation));
        }

        var dataRoot = Path.GetFullPath(applicationDataRoot);
        var pathRoot = Path.GetPathRoot(dataRoot);
        if (pathRoot is not null && string.Equals(
                dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application data root cannot be a filesystem root.");
        }

        Directory.CreateDirectory(dataRoot);
        RejectReparsePoint(dataRoot);
        var lockPath = Path.Combine(dataRoot, LockFileName);
        if (File.Exists(lockPath)) RejectReparsePoint(lockPath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new ApplicationDataInUseException(
                $"DCx data is in use. Close DCx and any other recovery window before {operation}.",
                exception);
        }

        try
        {
            stream.SetLength(0);
            var marker = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId};operation={operation}\n");
            stream.Write(marker);
            stream.Flush(flushToDisk: true);
            return new ApplicationDataOperationLease(dataRoot, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var lockPath = stream.Name;
        stream.Dispose();
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The application-data lock path cannot be a reparse point.");
        }
    }
}
