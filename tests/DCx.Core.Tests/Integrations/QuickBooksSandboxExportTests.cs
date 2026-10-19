using System.IO.Compression;
using System.Xml.Linq;
using DCx.Core.Content;
using DCx.Core.Documents;
using DCx.Core.Integrations;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace DCx.Core.Tests.Integrations;

public sealed class QuickBooksSandboxExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-qb-{Guid.NewGuid():N}");

    [Fact]
    public async Task SandboxReport_ExportsTableFormulaTypedDatesAndCurrencyWithoutFormulaInjection()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "quickbooks-sandbox-profit-loss.xlsx");
        var client = new FixtureClient(new QuickBooksSandboxReport(
            "sandbox-report-2026-01",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            "USD",
            true,
            [
                new QuickBooksReportRow("Subscription revenue", "Income", 3000.00m),
                new QuickBooksReportRow("=HYPERLINK(\"https://evil.test\")", "Untrusted account label", -10.25m),
                new QuickBooksReportRow("Payment fees", "Expense", -87.00m),
            ]));

        await new QuickBooksSandboxExportService().ExportProfitAndLossAsync(client, destination);

        Assert.Equal(ContentKind.Spreadsheet, new LocalContentAdmissionPolicy().Evaluate(destination).Kind);
        using (var document = SpreadsheetDocument.Open(destination, false))
        {
            var schemaErrors = new OpenXmlValidator(FileFormatVersions.Office2007)
                .Validate(document)
                .Select(error => $"{error.Path?.XPath}: {error.Description}")
                .ToArray();
            Assert.Empty(schemaErrors);
        }
        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.NotNull(archive.GetEntry("xl/worksheets/_rels/sheet1.xml.rels"));
        Assert.NotNull(archive.GetEntry("xl/tables/table1.xml"));
        Assert.NotNull(archive.GetEntry("xl/styles.xml"));
        Assert.NotNull(archive.GetEntry("docProps/core.xml"));
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = ReadDocument(archive.GetEntry("xl/worksheets/sheet1.xml")!);
        var cells = sheet.Descendants(spreadsheet + "c").ToDictionary(
            cell => (string)cell.Attribute("r")!,
            StringComparer.Ordinal);
        Assert.Equal("3", (string?)cells["B3"].Attribute("s"));
        Assert.Equal(
            "46023",
            cells["B3"].Element(spreadsheet + "v")!.Value);
        Assert.Equal("3", (string?)cells["D3"].Attribute("s"));
        Assert.Equal("2", (string?)cells["C5"].Attribute("s"));
        Assert.Equal("4", (string?)cells["C8"].Attribute("s"));
        Assert.Equal("2902.75", cells["C8"].Element(spreadsheet + "v")!.Value);
        var formulas = sheet.Descendants(spreadsheet + "f").Select(formula => formula.Value).ToArray();
        Assert.Equal(["SUBTOTAL(109,[Amount USD])"], formulas);
        Assert.DoesNotContain(formulas, formula => formula.Contains("HYPERLINK", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("=HYPERLINK(\"https://evil.test\")", cells["A6"].Descendants(spreadsheet + "t").Single().Value);

        var table = ReadDocument(archive.GetEntry("xl/tables/table1.xml")!).Root!;
        Assert.Equal("A4:C8", (string?)table.Attribute("ref"));
        Assert.Equal("1", (string?)table.Attribute("totalsRowCount"));
        Assert.Equal("A4:C7", (string?)table.Element(spreadsheet + "autoFilter")!.Attribute("ref"));
        var tableColumns = table.Element(spreadsheet + "tableColumns")!.Elements(spreadsheet + "tableColumn").ToArray();
        Assert.Equal(["Account", "Category", "Amount USD"], tableColumns.Select(column => (string)column.Attribute("name")!));
        Assert.Equal("sum", (string?)tableColumns[2].Attribute("totalsRowFunction"));

        var styles = ReadDocument(archive.GetEntry("xl/styles.xml")!);
        var formats = styles.Descendants(spreadsheet + "numFmt").ToDictionary(
            format => (string)format.Attribute("numFmtId")!,
            format => (string)format.Attribute("formatCode")!,
            StringComparer.Ordinal);
        Assert.Equal("$#,##0.00;[Red]-$#,##0.00", formats["164"]);
        Assert.Equal("yyyy-mm-dd", formats["165"]);
        var relationshipText = string.Join('\n', archive.Entries
            .Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal))
            .Select(Read));
        Assert.DoesNotContain("TargetMode=\"External\"", relationshipText, StringComparison.OrdinalIgnoreCase);
        var extracted = new OfficeTextExtractor().Extract(destination, ContentKind.Spreadsheet);
        Assert.Contains("QuickBooks sandbox Profit and Loss", extracted);
        Assert.Contains("Subscription revenue", extracted);
        Assert.Contains("SANDBOX", extracted);
    }

    [Fact]
    public async Task Export_RejectsNonSandboxOrNonUsdReport()
    {
        Directory.CreateDirectory(root);
        var report = new QuickBooksSandboxReport(
            "live-report", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), "USD", false,
            [new QuickBooksReportRow("Revenue", "Income", 1)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuickBooksSandboxExportService()
            .ExportProfitAndLossAsync(new FixtureClient(report), Path.Combine(root, "blocked.xlsx")));
    }

    [Fact]
    public async Task Export_RejectsUnsafeLabelsOrUnsupportedDatesWithoutReplacingDestination()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "preserved.xlsx");
        await File.WriteAllTextAsync(destination, "preserve-existing-export");
        var unsafeLabel = Report([
            new QuickBooksReportRow("Revenue\u202Efdp.exe", "Income", 1),
        ]);
        var unsupportedDate = new QuickBooksSandboxReport(
            "sandbox-old", new DateOnly(1899, 12, 31), new DateOnly(2026, 1, 31), "USD", true,
            [new QuickBooksReportRow("Revenue", "Income", 1)]);
        var service = new QuickBooksSandboxExportService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportProfitAndLossAsync(new FixtureClient(unsafeLabel), destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportProfitAndLossAsync(new FixtureClient(unsupportedDate), destination));

        Assert.Equal("preserve-existing-export", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Export_ReplacesExistingWorkbookAtomically()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "replace.xlsx");
        var service = new QuickBooksSandboxExportService();
        await service.ExportProfitAndLossAsync(
            new FixtureClient(Report([new QuickBooksReportRow("Old account", "Income", 1)])),
            destination);
        await service.ExportProfitAndLossAsync(
            new FixtureClient(Report([new QuickBooksReportRow("New account", "Income", 2)])),
            destination);

        var extracted = new OfficeTextExtractor().Extract(destination, ContentKind.Spreadsheet);
        Assert.Contains("New account", extracted);
        Assert.DoesNotContain("Old account", extracted);
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Export_CancellationPreservesExistingDestination()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "canceled.xlsx");
        await File.WriteAllTextAsync(destination, "preserve-on-cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new QuickBooksSandboxExportService().ExportProfitAndLossAsync(
                new FixtureClient(Report([new QuickBooksReportRow("Revenue", "Income", 1)])),
                destination,
                cancellation.Token));

        Assert.Equal("preserve-on-cancel", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Export_RejectsWrongExtensionNullRowsOrNullRow()
    {
        Directory.CreateDirectory(root);
        var service = new QuickBooksSandboxExportService();
        var valid = Report([new QuickBooksReportRow("Revenue", "Income", 1)]);
        var nullRows = valid with { Rows = null! };
        var nullRow = valid with { Rows = [null!] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportProfitAndLossAsync(new FixtureClient(valid), Path.Combine(root, "wrong.csv")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportProfitAndLossAsync(new FixtureClient(nullRows), Path.Combine(root, "null-rows.xlsx")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportProfitAndLossAsync(new FixtureClient(nullRow), Path.Combine(root, "null-row.xlsx")));
    }

    [Theory]
    [InlineData(1900, 1, 1, "1")]
    [InlineData(1900, 2, 28, "59")]
    [InlineData(1900, 3, 1, "61")]
    public async Task Export_UsesExcelDateSystemAcrossThe1900CompatibilityBoundary(
        int year,
        int month,
        int day,
        string expectedSerial)
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, $"excel-date-{year}-{month}-{day}.xlsx");
        var date = new DateOnly(year, month, day);
        var report = Report([new QuickBooksReportRow("Revenue", "Income", 1)]) with
        {
            PeriodStart = date,
            PeriodEnd = date,
        };

        await new QuickBooksSandboxExportService().ExportProfitAndLossAsync(
            new FixtureClient(report),
            destination);

        using var archive = ZipFile.OpenRead(destination);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = ReadDocument(archive.GetEntry("xl/worksheets/sheet1.xml")!);
        var startDate = sheet.Descendants(spreadsheet + "c")
            .Single(cell => string.Equals((string?)cell.Attribute("r"), "B3", StringComparison.Ordinal));
        Assert.Equal(expectedSerial, startDate.Element(spreadsheet + "v")!.Value);
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static XDocument ReadDocument(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static QuickBooksSandboxReport Report(IReadOnlyList<QuickBooksReportRow> rows) => new(
        "sandbox-report-2026-01",
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 31),
        "USD",
        true,
        rows);

    private sealed class FixtureClient(QuickBooksSandboxReport report) : IQuickBooksSandboxClient
    {
        public Task<QuickBooksSandboxReport> GetProfitAndLossAsync(CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }
}
