namespace DCx.Core.Tests;

internal static class TestDirectory
{
    public static void DeleteIfExists(string path)
    {
        var canonical = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Test cleanup is restricted to the operating-system temporary directory.");
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!Directory.Exists(canonical)) return;
            try
            {
                Directory.Delete(canonical, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
