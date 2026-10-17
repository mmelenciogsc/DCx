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
        ]);
        var html = Path.Combine(_root, "report.html");
        var docx = Path.Combine(_root, "report.docx");
        var exporter = new AccessibleDocumentExporter();

        await exporter.ExportHtmlAsync(document, html);
        exporter.ExportDocx(document, docx);

        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("<main><h1>Accessible report</h1>", htmlText, StringComparison.Ordinal);
        Assert.Contains("<label for=\"field\">Status</label><input id=\"field\" value=\"Draft\">", htmlText, StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(docx);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        Assert.NotNull(archive.GetEntry("word/document.xml"));
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
}
