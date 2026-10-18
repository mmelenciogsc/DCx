namespace DCx.Core.LocalModels;

internal static class LocalModelStoragePathGuard
{
    public static string EnsureDirectory(string path, string label) =>
        Guard(label, () => EnsureDirectoryCore(path, label));

    public static string RequireDirectory(string path, string label) =>
        Guard(label, () => RequireDirectoryCore(path, label));

    public static string ValidateFile(string path, bool mustExist, string label) =>
        Guard(label, () => ValidateFileCore(path, mustExist, label));

    public static void TryDeleteTemporary(string root, string path, string label)
    {
        try
        {
            RequireDirectory(root, label + " directory");
            var canonical = ValidateFile(path, mustExist: false, label);
            if (File.Exists(canonical)) File.Delete(canonical);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                InvalidOperationException)
        {
            // An unsafe or replaced directory is never followed merely to clean up a temporary file.
        }
    }

    private static string EnsureDirectoryCore(string path, string label)
    {
        var canonical = Canonicalize(path, label);
        RejectReparseChain(canonical, label);
        Directory.CreateDirectory(canonical);
        return RequireDirectoryCore(canonical, label);
    }

    private static string RequireDirectoryCore(string path, string label)
    {
        var canonical = Canonicalize(path, label);
        if (!Directory.Exists(canonical))
        {
            throw new InvalidOperationException($"The {label} directory is unavailable.");
        }

        RejectReparseChain(canonical, label);
        var attributes = File.GetAttributes(canonical);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The {label} directory is unsafe.");
        }

        return canonical;
    }

    private static string ValidateFileCore(string path, bool mustExist, string label)
    {
        var canonical = Canonicalize(path, label);
        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException($"The {label} file has no safe parent directory.");
        }

        RequireDirectoryCore(parent, label + " parent");
        if (!File.Exists(canonical))
        {
            if (Directory.Exists(canonical))
            {
                throw new InvalidOperationException($"The {label} path is a directory.");
            }

            if (mustExist)
            {
                throw new InvalidOperationException($"The {label} file is missing.");
            }

            return canonical;
        }

        var attributes = File.GetAttributes(canonical);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException($"The {label} file is unsafe.");
        }

        return canonical;
    }

    private static T Guard<T>(string label, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException($"The {label} path could not be validated safely.");
        }
    }

    private static string Canonicalize(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"The {label} path is invalid.");
        }

        var canonical = Path.GetFullPath(path);
        if (canonical.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {label} path must be local.");
        }

        var volumeRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(volumeRoot) || string.Equals(
                canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {label} path cannot be a filesystem root.");
        }

        return canonical;
    }

    private static void RejectReparseChain(string path, string label)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"The {label} path crosses a reparse point.");
            }
        }
    }
}
