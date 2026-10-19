using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;

namespace DCx.Core.Documents;

public sealed class AccessibleDocumentExporter
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly AccessibleDocumentValidator _validator = new();

    public async Task ExportHtmlAsync(AccessibleDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        EnsureValid(document);
        var canonical = PrepareOutputPath(outputPath, ".html");
        cancellationToken.ThrowIfCancellationRequested();
        var language = CultureInfo.GetCultureInfo(document.Language).Name;
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"").Append(WebUtility.HtmlEncode(language))
            .Append("\"><head><meta charset=\"utf-8\"><title>")
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
        var temporary = CreateTemporaryPath(canonical);
        try
        {
            await File.WriteAllTextAsync(temporary, builder.ToString(), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, canonical, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    public void ExportDocx(AccessibleDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        EnsureValid(document);
        var canonical = PrepareOutputPath(outputPath, ".docx");
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = CreateTemporaryPath(canonical);
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteDocxPackage(archive, document, cancellationToken);
                }

                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, canonical, overwrite: true);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
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

    private static void WriteDocxPackage(
        ZipArchive archive,
        AccessibleDocument document,
        CancellationToken cancellationToken)
    {
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
              <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "word/_rels/document.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        var language = CultureInfo.GetCultureInfo(document.Language).Name;
        WriteEntry(archive, "word/styles.xml", $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults><w:rPrDefault><w:rPr><w:lang w:val="{{language}}" w:eastAsia="{{language}}" w:bidi="{{language}}"/></w:rPr></w:rPrDefault><w:pPrDefault/></w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
              <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="10"/><w:qFormat/></w:style>
              <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
              <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="35"/><w:qFormat/></w:style>
              <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:uiPriority w:val="99"/></w:style>
            </w:styles>
            """);
        WriteCoreProperties(archive, document.Title, language);
        WriteEntry(archive, "docProps/app.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>DCx</Application></Properties>
            """);

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        var entry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument(true);
        writer.WriteStartElement("w", "document", WordNamespace);
        writer.WriteStartElement("w", "body", WordNamespace);
        WriteParagraph(writer, document.Title, "Title");
        foreach (var block in document.Blocks.OrderBy(block => block.PageNumber).ThenBy(block => block.ReadingOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (block.Role)
            {
                case DocumentBlockRole.Heading:
                    WriteParagraph(writer, block.Text, "Heading1");
                    break;
                case DocumentBlockRole.FormField:
                    WriteParagraph(writer, $"{block.AccessibleLabel}: {block.Text}", null);
                    break;
                case DocumentBlockRole.ImageDescription:
                    WriteParagraph(writer, FormatLabeledText(block), "Caption");
                    break;
                case DocumentBlockRole.Table:
                    WriteTable(writer, block);
                    break;
                default:
                    WriteParagraph(writer, block.Text, null);
                    break;
            }
        }

        writer.WriteStartElement("w", "sectPr", WordNamespace);
        writer.WriteStartElement("w", "pgSz", WordNamespace);
        writer.WriteAttributeString("w", "w", WordNamespace, "12240");
        writer.WriteAttributeString("w", "h", WordNamespace, "15840");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteCoreProperties(ZipArchive archive, string title, string language)
    {
        var entry = archive.CreateEntry("docProps/core.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
        });
        writer.WriteStartDocument(true);
        writer.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
        writer.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
        writer.WriteStartElement("dc", "title", "http://purl.org/dc/elements/1.1/");
        writer.WriteString(title);
        writer.WriteEndElement();
        writer.WriteStartElement("dc", "language", "http://purl.org/dc/elements/1.1/");
        writer.WriteString(language);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static string FormatLabeledText(DocumentBlock block) =>
        string.IsNullOrWhiteSpace(block.AccessibleLabel)
            ? block.Text
            : $"{block.AccessibleLabel}: {block.Text}";

    private static void WriteTable(XmlWriter writer, DocumentBlock block)
    {
        if (!string.IsNullOrWhiteSpace(block.AccessibleLabel))
            WriteParagraph(writer, block.AccessibleLabel, "Caption");
        writer.WriteStartElement("w", "tbl", WordNamespace);
        writer.WriteStartElement("w", "tblPr", WordNamespace);
        writer.WriteStartElement("w", "tblStyle", WordNamespace);
        writer.WriteAttributeString("w", "val", WordNamespace, "TableNormal");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("w", "tblGrid", WordNamespace);
        writer.WriteStartElement("w", "gridCol", WordNamespace);
        writer.WriteAttributeString("w", "w", WordNamespace, "9000");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("w", "tr", WordNamespace);
        writer.WriteStartElement("w", "tc", WordNamespace);
        writer.WriteStartElement("w", "tcPr", WordNamespace);
        writer.WriteEndElement();
        WriteParagraph(writer, block.Text, null);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteParagraph(XmlWriter writer, string text, string? style)
    {
        writer.WriteStartElement("w", "p", WordNamespace);
        if (style is not null)
        {
            writer.WriteStartElement("w", "pPr", WordNamespace);
            writer.WriteStartElement("w", "pStyle", WordNamespace);
            writer.WriteAttributeString("w", "val", WordNamespace, style);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteStartElement("w", "r", WordNamespace);
        writer.WriteStartElement("w", "t", WordNamespace);
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(text);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string PrepareOutputPath(string outputPath, string requiredExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var canonical = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(canonical), requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The accessible export must use a {requiredExtension} destination.");
        var directory = Path.GetDirectoryName(canonical)
                        ?? throw new InvalidOperationException("The export destination has no parent directory.");
        Directory.CreateDirectory(directory);
        return canonical;
    }

    private static string CreateTemporaryPath(string canonical) =>
        Path.Combine(Path.GetDirectoryName(canonical)!, $".{Path.GetFileName(canonical)}.{Guid.NewGuid():N}.tmp");

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original exception. A random, same-directory temporary contains only this export.
        }
    }
}
