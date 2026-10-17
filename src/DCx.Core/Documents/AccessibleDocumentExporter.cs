using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;

namespace DCx.Core.Documents;

public sealed class AccessibleDocumentExporter
{
    private readonly AccessibleDocumentValidator _validator = new();

    public async Task ExportHtmlAsync(AccessibleDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        EnsureValid(document);
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(document.Title))
            .Append("</title></head><body><main><h1>")
            .Append(WebUtility.HtmlEncode(document.Title))
            .Append("</h1>");
        foreach (var block in document.Blocks.OrderBy(block => block.PageNumber).ThenBy(block => block.ReadingOrder))
        {
            var text = WebUtility.HtmlEncode(block.Text);
            switch (block.Role)
            {
                case DocumentBlockRole.Heading:
                    builder.Append("<h2>").Append(text).Append("</h2>");
                    break;
                case DocumentBlockRole.FormField when block.IsEditable:
                    var id = WebUtility.HtmlEncode(block.Id);
                    builder.Append("<label for=\"").Append(id).Append("\">")
                        .Append(WebUtility.HtmlEncode(block.AccessibleLabel)).Append("</label>")
                        .Append("<input id=\"").Append(id).Append("\" value=\"").Append(text).Append("\">");
                    break;
                case DocumentBlockRole.ImageDescription:
                    builder.Append("<figure role=\"img\" aria-label=\"")
                        .Append(WebUtility.HtmlEncode(block.AccessibleLabel ?? block.Text))
                        .Append("\"><figcaption>").Append(text).Append("</figcaption></figure>");
                    break;
                case DocumentBlockRole.Table:
                    builder.Append("<table><caption>")
                        .Append(WebUtility.HtmlEncode(block.AccessibleLabel ?? "Table"))
                        .Append("</caption><tbody><tr><td>").Append(text).Append("</td></tr></tbody></table>");
                    break;
                default:
                    builder.Append("<p>").Append(text).Append("</p>");
                    break;
            }
        }

        builder.Append("</main></body></html>");
        await File.WriteAllTextAsync(Path.GetFullPath(outputPath), builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    public void ExportDocx(AccessibleDocument document, string outputPath)
    {
        EnsureValid(document);
        using var archive = ZipFile.Open(Path.GetFullPath(outputPath), ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        WriteEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        var entry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("w", "document", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        writer.WriteStartElement("w", "body", null);
        WriteParagraph(writer, document.Title, "Title");
        foreach (var block in document.Blocks.OrderBy(block => block.PageNumber).ThenBy(block => block.ReadingOrder))
            WriteParagraph(writer, block.Role == DocumentBlockRole.FormField ? $"{block.AccessibleLabel}: {block.Text}" : block.Text,
                block.Role == DocumentBlockRole.Heading ? "Heading1" : null);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private void EnsureValid(AccessibleDocument document)
    {
        var validation = _validator.Validate(document);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteParagraph(XmlWriter writer, string text, string? style)
    {
        writer.WriteStartElement("w", "p", null);
        if (style is not null)
        {
            writer.WriteStartElement("w", "pPr", null);
            writer.WriteStartElement("w", "pStyle", null);
            writer.WriteAttributeString("w", "val", null, style);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteStartElement("w", "r", null);
        writer.WriteStartElement("w", "t", null);
        writer.WriteString(text);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}
