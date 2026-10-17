namespace DCx.Core.Content;

public sealed class LocalContentAdmissionPolicy
{
    private static readonly IReadOnlyDictionary<string, (ContentKind Kind, long MaximumBytes)> Allowed =
        new Dictionary<string, (ContentKind, long)>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = (ContentKind.Text, 32L * 1024 * 1024),
            [".csv"] = (ContentKind.Csv, 256L * 1024 * 1024),
            [".pdf"] = (ContentKind.Pdf, 1024L * 1024 * 1024),
            [".docx"] = (ContentKind.Word, 512L * 1024 * 1024),
            [".xlsx"] = (ContentKind.Spreadsheet, 512L * 1024 * 1024),
            [".pptx"] = (ContentKind.Presentation, 512L * 1024 * 1024),
            [".png"] = (ContentKind.Image, 100L * 1024 * 1024),
            [".jpg"] = (ContentKind.Image, 100L * 1024 * 1024),
            [".jpeg"] = (ContentKind.Image, 100L * 1024 * 1024),
            [".wav"] = (ContentKind.Audio, 2L * 1024 * 1024 * 1024),
            [".mp3"] = (ContentKind.Audio, 2L * 1024 * 1024 * 1024),
            [".mp4"] = (ContentKind.Video, 10L * 1024 * 1024 * 1024),
        };

    public ContentAdmission Evaluate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Deny("A local file path is required.");
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Deny("The local file path is invalid.");
        }

        var file = new FileInfo(canonicalPath);
        if (!file.Exists)
        {
            return Deny("The local file does not exist.");
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return Deny("Reparse-point files require a separately approved resolution path.");
        }

        if (!Allowed.TryGetValue(file.Extension, out var rule))
        {
            return Deny("The file extension is not supported.");
        }

        if (file.Length <= 0 || file.Length > rule.MaximumBytes)
        {
            return Deny("The file is empty or exceeds the size limit for its type.");
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(header);
        if (!MatchesSignature(rule.Kind, header[..read]))
        {
            return Deny("The file signature does not match its declared type.");
        }

        return new ContentAdmission(true, rule.Kind, canonicalPath, "Extension, size and file signature accepted.");
    }

    private static bool MatchesSignature(ContentKind kind, ReadOnlySpan<byte> header) => kind switch
    {
        ContentKind.Text or ContentKind.Csv => !header.Contains((byte)0),
        ContentKind.Pdf => header.StartsWith("%PDF-"u8),
        ContentKind.Word or ContentKind.Spreadsheet or ContentKind.Presentation => header.StartsWith("PK\x03\x04"u8),
        ContentKind.Image => (header.Length >= 8 && header[0] == 0x89 && header[1..].StartsWith("PNG\r\n\x1a\n"u8)) ||
                             (header.Length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff),
        ContentKind.Audio => header.StartsWith("ID3"u8) ||
                             (header.StartsWith("RIFF"u8) && header.Length >= 12 && header[8..].StartsWith("WAVE"u8)) ||
                             (header.Length >= 2 && header[0] == 0xff && (header[1] & 0xe0) == 0xe0),
        ContentKind.Video => header.Length >= 12 && header[4..].StartsWith("ftyp"u8),
        _ => false,
    };

    private static ContentAdmission Deny(string reason) => new(false, null, null, reason);
}
