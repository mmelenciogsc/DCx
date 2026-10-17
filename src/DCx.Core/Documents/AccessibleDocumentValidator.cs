namespace DCx.Core.Documents;

public sealed class AccessibleDocumentValidator
{
    public DocumentValidationResult Validate(AccessibleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(document.Title)) errors.Add("A document title is required.");
        if (document.Blocks.Count == 0) errors.Add("At least one semantic block is required.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<(int Page, int Order)>();
        foreach (var block in document.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id)) errors.Add("Block identifiers must be nonempty and unique.");
            if (block.PageNumber < 1 || block.ReadingOrder < 0 || !orders.Add((block.PageNumber, block.ReadingOrder)))
                errors.Add($"Block {block.Id} has invalid or duplicate page reading order.");
            if (string.IsNullOrWhiteSpace(block.Text)) errors.Add($"Block {block.Id} has no text.");
            if (block.Bounds is not null && !block.Bounds.IsValid) errors.Add($"Block {block.Id} has invalid normalized geometry.");
            if (block.Role == DocumentBlockRole.FormField && string.IsNullOrWhiteSpace(block.AccessibleLabel))
                errors.Add($"Form field {block.Id} requires an accessible label.");
        }

        return new DocumentValidationResult(errors.Count == 0, errors);
    }
}
