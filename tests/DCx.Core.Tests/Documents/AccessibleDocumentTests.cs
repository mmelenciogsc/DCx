using System.IO.Compression;
using DCx.Core.Content;
using DCx.Core.Documents;

namespace DCx.Core.Tests.Documents;

public sealed class AccessibleDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dcx-documents-{Guid.NewGuid():N}");

    public AccessibleDocumentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Exporters_CreateSemanticHtmlAndRoundTrippableDocx()
    {
        var document = new AccessibleDocument("Accessible report",
        [
            new("heading", 1, 0, DocumentBlockRole.Heading, "Summary", new(0.1, 0.1, 0.8, 0.1)),
            new("paragraph", 1, 1, DocumentBlockRole.Paragraph, "Verified content.", new(0.1, 0.25, 0.8, 0.1)),
            new("field", 1, 2, DocumentBlockRole.FormField, "Draft", new(0.1, 0.4, 0.4, 0.1), "Status", true),
        ], "fil-PH");
        var html = Path.Combine(_root, "report.html");
        var docx = Path.Combine(_root, "report.docx");
        var exporter = new AccessibleDocumentExporter();

        await exporter.ExportHtmlAsync(document, html);
        exporter.ExportDocx(document, docx);

        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("<html lang=\"fil-PH\">", htmlText, StringComparison.Ordinal);
        Assert.Contains("<main><h1>Accessible report</h1>", htmlText, StringComparison.Ordinal);
        Assert.Contains("<label for=\"field\">Status</label><input id=\"field\" value=\"Draft\">", htmlText, StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(docx);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        Assert.NotNull(archive.GetEntry("word/document.xml"));
        Assert.NotNull(archive.GetEntry("word/styles.xml"));
        Assert.NotNull(archive.GetEntry("word/_rels/document.xml.rels"));
        Assert.NotNull(archive.GetEntry("docProps/core.xml"));
        Assert.NotNull(archive.GetEntry("docProps/app.xml"));
        var styles = Read(archive.GetEntry("word/styles.xml")!);
        Assert.Contains("w:styleId=\"Title\"", styles, StringComparison.Ordinal);
        Assert.Contains("w:styleId=\"Heading1\"", styles, StringComparison.Ordinal);
        Assert.Contains("w:outlineLvl w:val=\"0\"", styles, StringComparison.Ordinal);
        Assert.Contains("w:lang w:val=\"fil-PH\"", styles, StringComparison.Ordinal);
        var documentXml = Read(archive.GetEntry("word/document.xml")!);
        Assert.Contains("w:pStyle w:val=\"Title\"", documentXml, StringComparison.Ordinal);
        Assert.Contains("w:pStyle w:val=\"Heading1\"", documentXml, StringComparison.Ordinal);
        var core = Read(archive.GetEntry("docProps/core.xml")!);
        Assert.Contains("<dc:title>Accessible report</dc:title>", core, StringComparison.Ordinal);
        Assert.Contains("<dc:language>fil-PH</dc:language>", core, StringComparison.Ordinal);
        var extracted = new OfficeTextExtractor().Extract(docx, ContentKind.Word);
        Assert.Equal(["Accessible report", "Summary", "Verified content.", "Status: Draft"], extracted);
    }

    [Fact]
    public void Validator_RejectsUnlabeledEditableFieldAndInvalidGeometry()
    {
        var document = new AccessibleDocument("Fixture",
        [
            new("field", 1, 0, DocumentBlockRole.FormField, "value", new(0.9, 0.1, 0.2, 0.2), null, true),
        ]);

        var result = new AccessibleDocumentValidator().Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("geometry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("label", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsUnrecognizedDocumentLanguage()
    {
        var document = new AccessibleDocument(
            "Fixture",
            [new("paragraph", 1, 0, DocumentBlockRole.Paragraph, "Text")],
            "en_US!");

        var result = new AccessibleDocumentValidator().Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("language", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepairOverlay_LabelsKnownElementAndRejectsUnknownElement()
    {
        var document = new AccessibleDocument("Fixture",
        [new("icon", 1, 0, DocumentBlockRole.ImageDescription, "Warning symbol")]);
        var overlay = new SemanticRepairOverlay();

        var repaired = overlay.ApplyLabels(document, new Dictionary<string, string> { ["icon"] = "Warning" });

        Assert.Equal("Warning", repaired.Blocks[0].AccessibleLabel);
        Assert.Throws<ArgumentException>(() => overlay.ApplyLabels(document, new Dictionary<string, string> { ["missing"] = "Bad" }));
    }

    [Fact]
    public async Task HtmlExporter_EmitsSemanticRepairsForImageAndTable()
    {
        var document = new AccessibleDocument("Repaired",
        [
            new("icon", 1, 0, DocumentBlockRole.ImageDescription, "Triangle with exclamation mark", AccessibleLabel: "Warning icon"),
            new("table", 1, 1, DocumentBlockRole.Table, "North, 42", AccessibleLabel: "Regional totals"),
        ]);
        var output = Path.Combine(_root, "repaired.html");

        await new AccessibleDocumentExporter().ExportHtmlAsync(document, output);
        var html = await File.ReadAllTextAsync(output);

        Assert.Contains("<figure role=\"img\" aria-label=\"Warning icon\">", html, StringComparison.Ordinal);
        Assert.Contains("<table><caption>Regional totals</caption>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DocxExporter_PreservesReviewedImageAndTableSemantics()
    {
        var document = new AccessibleDocument("Repaired",
        [
            new("icon", 1, 0, DocumentBlockRole.ImageDescription, "Triangle with exclamation mark", AccessibleLabel: "Warning icon"),
            new("table", 1, 1, DocumentBlockRole.Table, "North, 42", AccessibleLabel: "Regional totals"),
        ]);
        var output = Path.Combine(_root, "repaired.docx");

        new AccessibleDocumentExporter().ExportDocx(document, output);

        using var archive = ZipFile.OpenRead(output);
        var xml = Read(archive.GetEntry("word/document.xml")!);
        Assert.Contains("w:pStyle w:val=\"Caption\"", xml, StringComparison.Ordinal);
        Assert.Contains("Warning icon: Triangle with exclamation mark", xml, StringComparison.Ordinal);
        Assert.Contains("Regional totals", xml, StringComparison.Ordinal);
        Assert.Contains("<w:tbl>", xml, StringComparison.Ordinal);
        Assert.Contains("North, 42", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetMode=\"External\"", string.Join('\n', archive.Entries.Select(Read)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exporters_ReplacePriorExportsAtomically()
    {
        var first = new AccessibleDocument("First", [new("first", 1, 0, DocumentBlockRole.Paragraph, "Old content")]);
        var second = new AccessibleDocument("Second", [new("second", 1, 0, DocumentBlockRole.Paragraph, "New content")]);
        var html = Path.Combine(_root, "replace.html");
        var docx = Path.Combine(_root, "replace.docx");
        var exporter = new AccessibleDocumentExporter();

        await exporter.ExportHtmlAsync(first, html);
        await exporter.ExportHtmlAsync(second, html);
        exporter.ExportDocx(first, docx);
        exporter.ExportDocx(second, docx);

        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("New content", htmlText, StringComparison.Ordinal);
        Assert.DoesNotContain("Old content", htmlText, StringComparison.Ordinal);
        Assert.Equal(["Second", "New content"], new OfficeTextExtractor().Extract(docx, ContentKind.Word));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Exporters_RejectUnsafeXmlOrCancellationWithoutReplacingDestination()
    {
        var html = Path.Combine(_root, "preserved.html");
        var docx = Path.Combine(_root, "preserved.docx");
        await File.WriteAllTextAsync(html, "preserve-html");
        await File.WriteAllTextAsync(docx, "preserve-docx");
        var unsafeDocument = new AccessibleDocument(
            "Unsafe",
            [new("block", 1, 0, DocumentBlockRole.Paragraph, "invalid\u0001xml")]);
        var safeDocument = new AccessibleDocument(
            "Safe",
            [new("block", 1, 0, DocumentBlockRole.Paragraph, "Safe text")]);
        var exporter = new AccessibleDocumentExporter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportHtmlAsync(unsafeDocument, html));
        Assert.Throws<InvalidOperationException>(() => exporter.ExportDocx(unsafeDocument, docx));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => exporter.ExportHtmlAsync(safeDocument, html, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => exporter.ExportDocx(safeDocument, docx, cancellation.Token));

        Assert.Equal("preserve-html", await File.ReadAllTextAsync(html));
        Assert.Equal("preserve-docx", await File.ReadAllTextAsync(docx));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Exporters_RequireMatchingAccessibleDocumentExtensions()
    {
        var document = new AccessibleDocument("Safe", [new("block", 1, 0, DocumentBlockRole.Paragraph, "Safe text")]);
        var exporter = new AccessibleDocumentExporter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportHtmlAsync(document, Path.Combine(_root, "wrong.txt")));
        Assert.Throws<InvalidOperationException>(() => exporter.ExportDocx(document, Path.Combine(_root, "wrong.zip")));
    }

    [Fact]
    public void FlattenedPdfLayout_PreservesReviewedGeometryAndSemanticText()
    {
        var document = new AccessibleDocument("Scanned & reviewed",
        [
            new("heading", 1, 0, DocumentBlockRole.Heading, "Account <summary>", new(0.1, 0.1, 0.8, 0.1)),
            new("field", 1, 1, DocumentBlockRole.FormField, "Draft & ready", new(0.2, 0.4, 0.5, 0.08), "Status", true),
            new("image", 2, 0, DocumentBlockRole.ImageDescription, "A chart", new(0.05, 0.1, 0.9, 0.7), "Revenue chart"),
        ], "fil-PH");

        var html = new AccessiblePdfHtmlBuilder().BuildFlattenedLayout(document);

        Assert.Contains("<html lang=\"fil-PH\">", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("<section class=\"page\" aria-label=\"Page 1\">", html, StringComparison.Ordinal);
        Assert.Contains("left:10%;top:10%;width:80%;height:10%", html, StringComparison.Ordinal);
        Assert.Contains("Account &lt;summary&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"field-label\">Status: </span><span>Draft &amp; ready</span>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"img\" aria-label=\"Revenue chart\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlattenedPdfLayout_RequiresGeometryAndSequentialPages()
    {
        var missingGeometry = new AccessibleDocument(
            "Missing geometry",
            [new("text", 1, 0, DocumentBlockRole.Paragraph, "Text")]);
        var missingPage = new AccessibleDocument(
            "Missing page",
            [new("text", 2, 0, DocumentBlockRole.Paragraph, "Text", new(0.1, 0.1, 0.8, 0.1))]);
        var builder = new AccessiblePdfHtmlBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.BuildFlattenedLayout(missingGeometry));
        Assert.Throws<InvalidOperationException>(() => builder.BuildFlattenedLayout(missingPage));
    }

    [Fact]
    public void OfficeExtractor_ReadsWordPresentationAndSpreadsheetFixtures()
    {
        var extractor = new OfficeTextExtractor();
        var docx = CreatePackage("sample.docx", "word/document.xml", "<w:document xmlns:w=\"w\"><w:p><w:r><w:t>Word text</w:t></w:r></w:p></w:document>");
        var pptx = CreatePackage("sample.pptx", "ppt/slides/slide1.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>Slide text</a:t></p:sld>");
        var xlsx = Path.Combine(_root, "sample.xlsx");
        using (var archive = ZipFile.Open(xlsx, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "xl/sharedStrings.xml", "<sst><si><t>Column heading</t></si></sst>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"inlineStr\"><is><t>Cell text</t></is></c></row></sheetData></worksheet>");
        }

        Assert.Equal(["Word text"], extractor.Extract(docx, ContentKind.Word));
        Assert.Equal(["Slide text"], extractor.Extract(pptx, ContentKind.Presentation));
        Assert.Equal(["Column heading", "Cell text"], extractor.Extract(xlsx, ContentKind.Spreadsheet));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreatePackage(string fileName, string entryName, string xml)
    {
        var path = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, entryName, xml);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
