using System.IO.Compression;
using DCx.Core.Content;
using DCx.Core.Documents;
using DCx.Core.Integrations;

namespace DCx.Core.Tests.Integrations;

public sealed class QuickBooksSandboxExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-qb-{Guid.NewGuid():N}");

    [Fact]
    public async Task SandboxReport_ExportsRealAccessibleXlsxWithoutFormulaInjection()
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
        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));
        var sheet = Read(archive.GetEntry("xl/worksheets/sheet1.xml")!);
        Assert.DoesNotContain("<f", sheet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("=HYPERLINK", sheet, StringComparison.Ordinal);
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

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class FixtureClient(QuickBooksSandboxReport report) : IQuickBooksSandboxClient
    {
        public Task<QuickBooksSandboxReport> GetProfitAndLossAsync(CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }
}
