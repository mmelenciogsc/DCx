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
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly DateOnly EarliestSupportedDate = new(1900, 1, 1);

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
        if (!string.Equals(Path.GetExtension(canonical), ".xlsx", StringComparison.OrdinalIgnoreCase))
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
                WriteWorkbook(output, report, cancellationToken);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, canonical, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original export result. The random temporary contains only this local export.
            }
        }
    }

    private static void Validate(QuickBooksSandboxReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.IsSandbox || !string.Equals(report.Currency, "USD", StringComparison.Ordinal) ||
            !IsSafeLabel(report.ReportId, 128) ||
            report.PeriodStart < EarliestSupportedDate || report.PeriodEnd < report.PeriodStart ||
            report.Rows is null || report.Rows.Count is 0 or > 10_000)
        {
            throw new InvalidOperationException("The QuickBooks report is not a valid USD sandbox fixture.");
        }

        foreach (var row in report.Rows)
        {
            if (row is null || !IsSafeLabel(row.Account, 512) || !IsSafeLabel(row.Category, 128) ||
                row.AmountUsd is < -1_000_000_000m or > 1_000_000_000m)
            {
                throw new InvalidOperationException("A QuickBooks report row is invalid or outside export limits.");
            }
        }
    }

    private static void WriteWorkbook(
        Stream output,
        QuickBooksSandboxReport report,
        CancellationToken cancellationToken)
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
              <Override PartName="/xl/tables/table1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
              <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
            </Types>
            """);
        WriteText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
            </Relationships>
            """);
        WriteText(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Profit and Loss" sheetId="1" r:id="rId1"/></sheets>
              <calcPr fullCalcOnLoad="1" forceFullCalc="1"/>
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
              <numFmts count="2"><numFmt numFmtId="164" formatCode="$#,##0.00;[Red]-$#,##0.00"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd"/></numFmts>
              <fonts count="2"><font/><font><b/></font></fonts>
              <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
              <borders count="1"><border/></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyNumberFormat="1"/></cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
              <dxfs count="0"/>
              <tableStyles count="0" defaultTableStyle="TableStyleMedium2" defaultPivotStyle="PivotStyleLight16"/>
            </styleSheet>
            """);
        WriteText(archive, "xl/worksheets/_rels/sheet1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/table" Target="../tables/table1.xml"/>
            </Relationships>
            """);
        WriteText(archive, "docProps/app.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>DCx</Application></Properties>
            """);
        WriteCoreProperties(archive);
        WriteTable(archive, report.Rows.Count);
        WriteWorksheet(archive, report, cancellationToken);
    }

    private static void WriteWorksheet(
        ZipArchive archive,
        QuickBooksSandboxReport report,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
        });
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        var dataEndRow = report.Rows.Count + 4;
        var totalRow = dataEndRow + 1;
        writer.WriteStartElement("dimension");
        writer.WriteAttributeString("ref", $"A1:D{totalRow}");
        writer.WriteEndElement();
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
        writer.WriteStartElement("cols");
        WriteColumn(writer, 1, 38);
        WriteColumn(writer, 2, 26);
        WriteColumn(writer, 3, 18);
        WriteColumn(writer, 4, 14);
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, [("A1", "QuickBooks sandbox Profit and Loss", 1)]);
        WriteRow(writer, 2, [("A2", "Report ID", 1), ("B2", report.ReportId, 0), ("C2", "SANDBOX", 1)]);
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", "3");
        WriteInlineStringCell(writer, "A3", "Period start", 1);
        WriteNumberCell(writer, "B3", ToExcelSerial(report.PeriodStart), 3);
        WriteInlineStringCell(writer, "C3", "Period end", 1);
        WriteNumberCell(writer, "D3", ToExcelSerial(report.PeriodEnd), 3);
        writer.WriteEndElement();
        WriteRow(writer, 4, [("A4", "Account", 1), ("B4", "Category", 1), ("C4", "Amount USD", 1)]);
        for (var index = 0; index < report.Rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = index + 5;
            var row = report.Rows[index];
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", number.ToString(CultureInfo.InvariantCulture));
            WriteInlineStringCell(writer, $"A{number}", row.Account, 0);
            WriteInlineStringCell(writer, $"B{number}", row.Category, 0);
            WriteNumberCell(writer, $"C{number}", row.AmountUsd, 2);
            writer.WriteEndElement();
        }

        var total = report.Rows.Aggregate(0m, (sum, row) => checked(sum + row.AmountUsd));
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", totalRow.ToString(CultureInfo.InvariantCulture));
        WriteInlineStringCell(writer, $"A{totalRow}", "Total", 1);
        WriteFormulaCell(writer, $"C{totalRow}", "SUBTOTAL(109,[Amount USD])", total, 4);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("tableParts");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("tablePart");
        writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteTable(ZipArchive archive, int rowCount)
    {
        var dataEndRow = rowCount + 4;
        var totalRow = dataEndRow + 1;
        var entry = archive.CreateEntry("xl/tables/table1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
        });
        writer.WriteStartDocument(true);
        writer.WriteStartElement("table", SpreadsheetNamespace);
        writer.WriteAttributeString("id", "1");
        writer.WriteAttributeString("name", "ProfitAndLossTable");
        writer.WriteAttributeString("displayName", "ProfitAndLossTable");
        writer.WriteAttributeString("ref", $"A4:C{totalRow}");
        writer.WriteAttributeString("totalsRowCount", "1");
        writer.WriteStartElement("autoFilter");
        writer.WriteAttributeString("ref", $"A4:C{dataEndRow}");
        writer.WriteEndElement();
        writer.WriteStartElement("tableColumns");
        writer.WriteAttributeString("count", "3");
        WriteTableColumn(writer, 1, "Account", totalsRowLabel: "Total");
        WriteTableColumn(writer, 2, "Category");
        WriteTableColumn(writer, 3, "Amount USD", totalsRowFunction: "sum");
        writer.WriteEndElement();
        writer.WriteStartElement("tableStyleInfo");
        writer.WriteAttributeString("name", "TableStyleMedium2");
        writer.WriteAttributeString("showFirstColumn", "0");
        writer.WriteAttributeString("showLastColumn", "0");
        writer.WriteAttributeString("showRowStripes", "1");
        writer.WriteAttributeString("showColumnStripes", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteTableColumn(
        XmlWriter writer,
        int id,
        string name,
        string? totalsRowLabel = null,
        string? totalsRowFunction = null)
    {
        writer.WriteStartElement("tableColumn");
        writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", name);
        if (totalsRowLabel is not null) writer.WriteAttributeString("totalsRowLabel", totalsRowLabel);
        if (totalsRowFunction is not null) writer.WriteAttributeString("totalsRowFunction", totalsRowFunction);
        writer.WriteEndElement();
    }

    private static void WriteCoreProperties(ZipArchive archive)
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
        writer.WriteString("QuickBooks sandbox Profit and Loss");
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

    private static void WriteNumberCell(XmlWriter writer, string reference, decimal value, int style)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("v");
        writer.WriteString(value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFormulaCell(
        XmlWriter writer,
        string reference,
        string formula,
        decimal cachedValue,
        int style)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("f");
        writer.WriteString(formula);
        writer.WriteEndElement();
        writer.WriteStartElement("v");
        writer.WriteString(cachedValue.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteColumn(XmlWriter writer, int index, decimal width)
    {
        writer.WriteStartElement("col");
        writer.WriteAttributeString("min", index.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("max", index.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("customWidth", "1");
        writer.WriteEndElement();
    }

    private static decimal ToExcelSerial(DateOnly value)
    {
        var serial = value.DayNumber - new DateOnly(1899, 12, 31).DayNumber;
        // Excel preserves Lotus 1-2-3's nonexistent 1900-02-29 at serial 60.
        return value >= new DateOnly(1900, 3, 1) ? serial + 1 : serial;
    }

    private static bool IsSafeLabel(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim() ||
            value.Any(character => char.IsControl(character) || IsUnsafeDirectionalControl(character)))
        {
            return false;
        }

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

    private static bool IsUnsafeDirectionalControl(char value) =>
        value is '\u200E' or '\u200F' or
            >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069';

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
