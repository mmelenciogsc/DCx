namespace DCx.Core.GlobalKnowledge;

internal static class GlobalStoragePathGuard
{
    public static string EnsureDirectory(string path, string label) =>
        Guard(label, () => EnsureDirectoryCore(path, label));

    public static string RequireDirectory(string path, string label) =>
        Guard(label, () => RequireDirectoryCore(path, label));

    public static string ValidateFile(string path, bool mustExist, string label) =>
        Guard(label, () => ValidateFileCore(path, mustExist, label));

    private static string EnsureDirectoryCore(string path, string label)
    {
        var canonical = Canonicalize(path, label);
        RejectReparseChain(canonical, label);
        Directory.CreateDirectory(canonical);
        RequireDirectoryCore(canonical, label);
        return canonical;
    }

    private static string RequireDirectoryCore(string path, string label)
    {
        var canonical = Canonicalize(path, label);
        if (!Directory.Exists(canonical))
        {
            throw new GlobalReplicaException($"The {label} directory is unavailable.");
        }

        RejectReparseChain(canonical, label);
        var attributes = File.GetAttributes(canonical);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new GlobalReplicaException($"The {label} directory is unsafe.");
        }

        return canonical;
    }

    private static string ValidateFileCore(string path, bool mustExist, string label)
    {
        var canonical = Canonicalize(path, label);
        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(parent))
        {
            throw new GlobalReplicaException($"The {label} file has no safe parent directory.");
        }

        RequireDirectoryCore(parent, label + " parent");
        var exists = File.Exists(canonical);
        if (!exists)
        {
            if (Directory.Exists(canonical))
            {
                throw new GlobalReplicaException($"The {label} path is a directory.");
            }

            if (mustExist)
            {
                throw new GlobalReplicaException($"The {label} file is missing.");
            }

            return canonical;
        }

        var attributes = File.GetAttributes(canonical);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new GlobalReplicaException($"The {label} file is unsafe.");
        }

        return canonical;
    }

    private static string Guard(string label, Func<string> operation)
    {
        try
        {
            return operation();
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new GlobalReplicaException($"The {label} path could not be validated safely.");
        }
    }

    private static string Canonicalize(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new GlobalReplicaException($"The {label} path is invalid.");
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GlobalReplicaException($"The {label} path is invalid.");
        }

        if (canonical.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new GlobalReplicaException($"The {label} path must be local.");
        }

        var volumeRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(volumeRoot) ||
            string.Equals(
                canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new GlobalReplicaException($"The {label} path cannot be a filesystem root.");
        }

        return canonical;
    }

    private static void RejectReparseChain(string path, string label)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new GlobalReplicaException($"The {label} path crosses a reparse point.");
            }
        }
    }
}
