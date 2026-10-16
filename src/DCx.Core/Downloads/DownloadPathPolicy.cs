namespace DCx.Core.Downloads;

public sealed class DownloadPathPolicy
{
    public string GetAvailablePath(string downloadDirectory, string? suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        var fileName = Path.GetFileName(suggestedFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
        }

        foreach (var character in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(character, '_');
        }

        fileName = fileName.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
        }

        var candidate = Path.Combine(downloadDirectory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 1; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(downloadDirectory, $"{stem} ({suffix}){extension}");
        }

        return candidate;
    }
}

