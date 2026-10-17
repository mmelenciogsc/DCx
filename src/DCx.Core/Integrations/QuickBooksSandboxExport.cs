using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DCx.Core.Integrations;

public sealed record QuickBooksReportRow(string Account, string Category, decimal AmountUsd);

public sealed record QuickBooksSandboxReport(
    string ReportId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    bool IsSandbox,
    IReadOnlyList<QuickBooksReportRow> Rows);

public interface IQuickBooksSandboxClient
{
    Task<QuickBooksSandboxReport> GetProfitAndLossAsync(CancellationToken cancellationToken);
}

public sealed class QuickBooksSandboxExportService
{
    public async Task ExportProfitAndLossAsync(
        IQuickBooksSandboxClient client,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var report = await client.GetProfitAndLossAsync(cancellationToken).ConfigureAwait(false);
        Validate(report);
        var canonical = Path.GetFullPath(destination);
        if (!canonical.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The QuickBooks sandbox export must use an .xlsx destination.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        var temporary = $"{canonical}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                WriteWorkbook(output, report);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, canonical, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Validate(QuickBooksSandboxReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.IsSandbox || !string.Equals(report.Currency, "USD", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(report.ReportId) || report.ReportId.Length > 128 ||
            report.PeriodEnd < report.PeriodStart || report.Rows.Count is 0 or > 10_000)
        {
            throw new InvalidOperationException("The QuickBooks report is not a valid USD sandbox fixture.");
        }

        foreach (var row in report.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Account) || row.Account.Length > 512 || row.Account.Contains('\0') ||
                string.IsNullOrWhiteSpace(row.Category) || row.Category.Length > 128 || row.Category.Contains('\0') ||
                row.AmountUsd is < -1_000_000_000m or > 1_000_000_000m)
            {
                throw new InvalidOperationException("A QuickBooks report row is invalid or outside export limits.");
            }
        }
    }

    private static void WriteWorkbook(Stream output, QuickBooksSandboxReport report)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteText(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteText(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Profit and Loss" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteText(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        WriteText(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font/><font><b/></font></fonts>
              <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
              <borders count="1"><border/></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
            </styleSheet>
            """);

        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
        });
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "4");
        writer.WriteAttributeString("topLeftCell", "A5");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, [("A1", "QuickBooks sandbox Profit and Loss", 1), ("B1", string.Empty, 0), ("C1", string.Empty, 0)]);
        WriteRow(writer, 2, [("A2", "Report ID", 1), ("B2", report.ReportId, 0), ("C2", "SANDBOX", 1)]);
        WriteRow(writer, 3, [("A3", "Period", 1), ("B3", report.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0), ("C3", report.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0)]);
        WriteRow(writer, 4, [("A4", "Account", 1), ("B4", "Category", 1), ("C4", "Amount USD", 1)]);
        for (var index = 0; index < report.Rows.Count; index++)
        {
            var number = index + 5;
            var row = report.Rows[index];
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
            WriteInlineStringCell(writer, $"A{number}", row.Account, 0);
            WriteInlineStringCell(writer, $"B{number}", row.Category, 0);
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", $"C{number}");
            writer.WriteAttributeString("s", "2");
            writer.WriteStartElement("v");
            writer.WriteString(row.AmountUsd.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("autoFilter");
        writer.WriteAttributeString("ref", $"A4:C{report.Rows.Count + 4}");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter writer, int number, (string Cell, string Value, int Style)[] cells)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
        foreach (var cell in cells)
        {
            WriteInlineStringCell(writer, cell.Cell, cell.Value, cell.Style);
        }

        writer.WriteEndElement();
    }

    private static void WriteInlineStringCell(XmlWriter writer, string reference, string value, int style)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("is");
        writer.WriteStartElement("t");
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(value);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
