namespace DCx.Core.Content;

public enum ContentKind
{
    Text,
    Csv,
    Pdf,
    Word,
    Spreadsheet,
    Presentation,
    Image,
    Audio,
    Video,
}

public sealed record ContentAdmission(bool IsAllowed, ContentKind? Kind, string? CanonicalPath, string Reason);
