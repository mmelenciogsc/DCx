using System.Globalization;
using System.Xml;

namespace DCx.Core.Documents;

public sealed class AccessibleDocumentValidator
{
    private const int MaximumBlocks = 50_000;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumIdCharacters = 256;
    private const int MaximumBlockTextCharacters = 1_000_000;
    private const int MaximumLabelCharacters = 4_096;
    private const long MaximumDocumentTextCharacters = 32L * 1024 * 1024;
    private const int MaximumReportedErrors = 100;

    public DocumentValidationResult Validate(AccessibleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        AddTextError(errors, document.Title, "Document title", MaximumTitleCharacters, required: true);
        if (!IsSupportedLanguage(document.Language))
            AddError(errors, "Document language must be a recognized non-invariant language tag.");
        if (document.Blocks is null)
        {
            AddError(errors, "A semantic block collection is required.");
            return new DocumentValidationResult(false, errors);
        }

        if (document.Blocks.Count == 0) AddError(errors, "At least one semantic block is required.");
        if (document.Blocks.Count > MaximumBlocks)
            AddError(errors, $"The document exceeds the {MaximumBlocks} block export limit.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<(int Page, int Order)>();
        long totalTextCharacters = document.Title?.Length ?? 0;
        foreach (var block in document.Blocks.Take(MaximumBlocks))
        {
            if (block is null)
            {
                AddError(errors, "Document blocks cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(block.Id) || block.Id.Length > MaximumIdCharacters ||
                !HasOnlyXmlCharacters(block.Id) || !ids.Add(block.Id))
                AddError(errors, "Block identifiers must be nonempty, unique, bounded and valid for XML export.");
            if (block.PageNumber < 1 || block.ReadingOrder < 0 || !orders.Add((block.PageNumber, block.ReadingOrder)))
                AddError(errors, $"Block {DisplayId(block.Id)} has invalid or duplicate page reading order.");
            AddTextError(errors, block.Text, $"Block {DisplayId(block.Id)} text", MaximumBlockTextCharacters, required: true);
            AddTextError(errors, block.AccessibleLabel, $"Block {DisplayId(block.Id)} accessible label", MaximumLabelCharacters, required: false);
            totalTextCharacters += block.Text?.Length ?? 0;
            totalTextCharacters += block.AccessibleLabel?.Length ?? 0;
            if (block.Bounds is not null && !block.Bounds.IsValid)
                AddError(errors, $"Block {DisplayId(block.Id)} has invalid normalized geometry.");
            if (block.Role == DocumentBlockRole.FormField && string.IsNullOrWhiteSpace(block.AccessibleLabel))
                AddError(errors, $"Form field {DisplayId(block.Id)} requires an accessible label.");

            if (errors.Count >= MaximumReportedErrors) break;
        }

        if (totalTextCharacters > MaximumDocumentTextCharacters)
            AddError(errors, $"The document exceeds the {MaximumDocumentTextCharacters} character export limit.");

        return new DocumentValidationResult(errors.Count == 0, errors);
    }

    private static void AddTextError(
        ICollection<string> errors,
        string? value,
        string name,
        int maximumCharacters,
        bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            AddError(errors, $"{name} is required.");
            return;
        }

        if (value is null) return;
        if (value.Length > maximumCharacters)
            AddError(errors, $"{name} exceeds the {maximumCharacters} character export limit.");
        if (!HasOnlyXmlCharacters(value))
            AddError(errors, $"{name} contains a character that cannot be represented safely in XML.");
    }

    private static bool HasOnlyXmlCharacters(string value)
    {
        try
        {
            XmlConvert.VerifyXmlChars(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsSupportedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 64 || !HasOnlyXmlCharacters(language))
            return false;
        try
        {
            return !string.IsNullOrEmpty(CultureInfo.GetCultureInfo(language).Name);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static string DisplayId(string? id) => string.IsNullOrWhiteSpace(id) ? "(missing identifier)" : id;

    private static void AddError(ICollection<string> errors, string error)
    {
        if (errors.Count < MaximumReportedErrors) errors.Add(error);
    }
}
