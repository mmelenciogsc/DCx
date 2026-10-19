namespace DCx.Core.Documents;

public enum DocumentBlockRole
{
    Heading,
    Paragraph,
    Table,
    FormField,
    ImageDescription,
}

public sealed record NormalizedRectangle(double X, double Y, double Width, double Height)
{
    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

public sealed record DocumentBlock(
    string Id,
    int PageNumber,
    int ReadingOrder,
    DocumentBlockRole Role,
    string Text,
    NormalizedRectangle? Bounds = null,
    string? AccessibleLabel = null,
    bool IsEditable = false);

public sealed record AccessibleDocument(
    string Title,
    IReadOnlyList<DocumentBlock> Blocks,
    string Language = "en-US");

public sealed record DocumentValidationResult(bool IsValid, IReadOnlyList<string> Errors);
